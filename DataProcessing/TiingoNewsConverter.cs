﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using QuantConnect;
using QuantConnect.Util;
using QuantConnect.Logging;
using Newtonsoft.Json.Linq;
using QuantConnect.Data;
using QuantConnect.DataSource;

namespace QuantConnect.DataProcessing
{
    public class TiingoNewsConverter : IDisposable
    {
        /// <summary>
        /// For backtesting, an offset to add to <see cref="BaseData.Time"/>
        /// </summary>
        /// <remarks>
        /// Old data (eg 2014 PublishedDate) can have newer crawl date (eg 2019)
        /// for these cases, where the diff is > 1 day, for backtesting,
        /// we use as <see cref="BaseData.Time"/> the published date of a piece of news.
        /// But doing so would be optimistic since it means algorithms
        /// will get the news immediately, so we add this offset.
        /// Live trading uses as <see cref="BaseData.Time"/> the crawler date.
        /// <see cref="TiingoNewsJsonConverter"/>
        /// </remarks>
        public static TimeSpan HistoricalCrawlOffset { get; } = TimeSpan.FromHours(1);

        private const int TaskCountLimit = 200;
        private readonly DirectoryInfo _sourceDirectory;
        private readonly DirectoryInfo _rootDestinationDirectory;
        private readonly DirectoryInfo _contentDirectory;
        // date to process, if null will process all data
        private readonly DateTime? _date;

        /// <summary>
        /// Creates an instance of the converter
        /// </summary>
        /// <param name="sourceDirectory">Directory to read raw data from</param>
        /// <param name="destinationDirectory">Directory to write processed data to</param>
        /// <param name="date">The date we want to process, if null will process all data</param>
        public TiingoNewsConverter(DirectoryInfo sourceDirectory, DirectoryInfo destinationDirectory, DateTime? date = null)
        {
            _date = date;
            _sourceDirectory = new DirectoryInfo(Path.Combine(sourceDirectory.FullName, "alternative", "tiingo"));
            _rootDestinationDirectory = Directory.CreateDirectory(Path.Combine(destinationDirectory.FullName, "alternative", "tiingo"));
            _contentDirectory = Directory.CreateDirectory(Path.Combine(_rootDestinationDirectory.FullName, "content"));
        }

        public bool Convert()
        {
            try
            {
                // supposing sourceFiles are the different daily files, eg: bulkfile_2014-01-11_2014-01-12.tar.gz
                // when a date is specified we load 2 files, eg for 20140112, load file_2014-01-11_2014-01-12 & file_2014-01-12_2014-01-13
                var sourceFiles = _sourceDirectory.EnumerateFiles()
                    .OrderBy(info => info.Name)
                    .Where(info => !_date.HasValue || info.Name.Contains(_date.ToStringInvariant("yyyy-MM-dd")))
                    .ToList(info => info);

                if (_date.HasValue && sourceFiles.Count != 2)
                {
                    throw new InvalidOperationException(
                        $"Expected exactly 2 source files for date {_date.Value} but found: {string.Join(",", sourceFiles)}");
                }

                var ioTasks = new Queue<Task>();
                var indexesPerTicker = new Dictionary<TickerIndex, List<Article>>();
                var newsPerDateCollection = new Dictionary<DateTime, List<Article>>();
                var currentDate = DateTime.MinValue;

                Log.Trace($"TiingoNewsConverter.Convert(): untar daily files. Count {sourceFiles.Count}...");
                foreach (var bulkFilePerDate in sourceFiles)
                {
                    Log.Trace($"TiingoNewsConverter.Convert(): file: {bulkFilePerDate.Name}...");
                    var tempPath = TemporaryPathProvider.Get();
                    Compression.UnTarGzFiles(bulkFilePerDate.FullName, tempPath);

                    // we expect 1 bulk json file for each date
                    var newsForDateFile = Directory.EnumerateFiles(tempPath).Single();

                    var jsonNews2 = JsonConvert.DeserializeObject(File.ReadAllText(newsForDateFile)) as JArray;
                    if (jsonNews2 == null)
                    {
                        Log.Error($"TiingoNewsConverter.Convert(): Failed to deserialize file: {bulkFilePerDate.Name}");
                        continue;
                    }

                    // this is required else memory grows for ever
                    if (ioTasks.Count > TaskCountLimit)
                    {
                        WaitForTasksToFinish(ioTasks);
                    }

                    Log.Trace("TiingoNewsConverter.Convert(): processing news...");
                    foreach (var jNews in jsonNews2)
                    {
                        var singleNewsData = TiingoNewsJsonConverter.DeserializeNews(jNews);

                        singleNewsData.Time = singleNewsData.CrawlDate;
                        if (singleNewsData.CrawlDate - singleNewsData.PublishedDate > Time.OneDay)
                        {
                            // old data (eg 2014 PublishedDate) can have newer crawl date (eg 2019)
                            // for these cases, for backtesting, use published time + 'HistoricalCrawlOffset'
                            singleNewsData.Time = singleNewsData.PublishedDate.Add(HistoricalCrawlOffset);
                        }
                        // we add the QC time into the news, this will be serialized and stored in the content.zip
                        // in backtesting this time will be used as time/endtime
                        jNews["time"] = singleNewsData.Time;

                        // we use QC time date
                        var newsDate = singleNewsData.Time.Date;

                        if (_date.HasValue && newsDate.Date != _date)
                        {
                            // skip news that were published another date than the one we want to process
                            continue;
                        }

                        // we store the data after 1 day difference
                        // raw data is not really ordered and can have jumps +-1 day
                        // files are generated at 12am EST and PublishDate is UTC
                        // we store files by UTC
                        if (newsDate > (currentDate + Time.OneDay))
                        {
                            var newsToStore = newsPerDateCollection.Where(kvp => kvp.Key < currentDate).ToList();
                            foreach (var news in newsToStore)
                            {
                                Log.Trace($"TiingoNewsConverter.Convert(): StoreDataForDate {news.Key}...");
                                StoreDataForDate(news.Key, indexesPerTicker, news.Value, ioTasks);
                                newsPerDateCollection.Remove(news.Key);
                            }

                            currentDate = newsDate;
                        }

                        // just in case: we don't expect published dates to go back in time more than 1 day
                        // if they do we want to know about it
                        if (newsDate < (currentDate - Time.OneDay))
                        {
                            throw new InvalidOperationException(
                                $"Unexpected date {newsDate} current at {currentDate} file {bulkFilePerDate.Name}"
                            );
                        }

                        if (singleNewsData.Symbols.Count == 0)
                        {
                            // skip articles with not symbols
                            continue;
                        }

                        var article = new Article(
                            singleNewsData.ArticleID + ".json",
                            singleNewsData.Time, // QC time
                            // Formatting.None -> 1 line
                            jNews.ToString(Formatting.None)
                        );

                        // store article by QC date time
                        List<Article> newsForDate;
                        if (!newsPerDateCollection.TryGetValue(newsDate, out newsForDate))
                        {
                            newsPerDateCollection[newsDate] = newsForDate = new List<Article>();
                        }
                        newsForDate.Add(article);

                        // update tickers indexes adding the new article id
                        foreach (var newsDataSymbol in singleNewsData.Symbols
                            // skip symbols which only have numbers as Value
                            .Where(symbol => !symbol.Value.All(char.IsDigit)))
                        {
                            var indexCacheKey = new TickerIndex(newsDataSymbol.Value, newsDate);

                            List<Article> articles;
                            if (!indexesPerTicker.TryGetValue(indexCacheKey, out articles))
                            {
                                indexesPerTicker[indexCacheKey] = articles = new List<Article>();
                            }
                            articles.Add(article);
                        }
                    }
                }

                foreach (var news in newsPerDateCollection)
                {
                    Log.Trace("TiingoNewsConverter.Convert(): store remaining data...");
                    StoreDataForDate(news.Key, indexesPerTicker, news.Value, ioTasks);
                }

                WaitForTasksToFinish(ioTasks);
                // after all tasks finished clean up
                TemporaryPathProvider.Delete();
            }
            catch (Exception exception)
            {
                Log.Error(exception);
                TemporaryPathProvider.Delete();
                return false;
            }
            return true;
        }

        private void StoreDataForDate(DateTime date,
            Dictionary<TickerIndex, List<Article>> indexesPerTicker,
            List<Article> newsForDate,
            Queue<Task> ioTasks)
        {
            var newsDateStr = date.ToStringInvariant(DateFormat.EightCharacter);

            var indexesToStore = indexesPerTicker.Where(index => index.Key.Date == date).ToList();
            foreach (var kvp in indexesToStore)
            {
                var indexKey = kvp.Key;
                // Store index: this is slow so send it to a task
                ioTasks.Enqueue(Task.Run(() =>
                {
                    try
                    {
                        // we have to order the articles here when we are about to store them by QC time
                        var orderedArticles = kvp.Value.OrderBy(article => article.Time).ToList();
                        var data = string.Join(Environment.NewLine, orderedArticles.Select(article => article.ID));

                        // the ticker directory
                        var tickerDir = Directory.CreateDirectory(
                                Path.Combine(_rootDestinationDirectory.FullName, indexKey.Ticker.ToLowerInvariant()));

                        // the index file for that ticker for that date
                        var indexFile = Path.Combine(tickerDir.FullName, $"{newsDateStr}.csv");

                        if (File.Exists(indexFile))
                        {
                            Log.Error($"TiingoNewsConverter.Convert(): Warning index file already exists: {indexFile}. Will overwrite...");
                        }

                        File.WriteAllText(indexFile, data);
                    }
                    catch (Exception exception)
                    {
                        Log.Error($"TiingoNewsConverter.Convert(): Failed to store index: {indexKey}", exception);
                    }
                }));

                indexesPerTicker.Remove(indexKey);
            }

            if (newsForDate.Count > 0)
            {
                // Store news for date: this is slow so send it to a task too
                ioTasks.Enqueue(Task.Run(() =>
                {
                    var data = newsForDate.ToDictionary(article => article.ID, article => article.RawData);
                    var contentPath = Path.Combine(_contentDirectory.FullName, $"{newsDateStr}.zip");
                    if (!Compression.ZipData(contentPath, data))
                    {
                        Log.Error($"TiingoNewsConverter.Convert(): Failed to store news: {contentPath}");
                    }
                }));
            }
        }

        /// <summary>
        /// Helper class that contains a Tiingo news article
        /// </summary>
        private class Article
        {
            /// <summary>
            /// The news article ID, used for creating indexes
            /// </summary>
            public string ID { get; }

            /// <summary>
            /// The news json raw data to store
            /// </summary>
            public string RawData { get; }

            /// <summary>
            /// This is the QC time
            /// </summary>
            public DateTime Time { get; }

            public Article(string id, DateTime time, string rawData)
            {
                ID = id;
                Time = time;
                RawData = rawData;
            }
        }

        /// <summary>
        /// Helper class used to store a Tickers which has news for a date
        /// </summary>
        private class TickerIndex
        {
            public string Ticker { get; }
            public DateTime Date { get; }

            public TickerIndex(string ticker, DateTime date)
            {
                Ticker = ticker;
                Date = date;
            }

            public override int GetHashCode()
            {
                return Ticker.GetHashCode() + Date.GetHashCode();
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                var objectAsType = obj as TickerIndex;
                if (objectAsType == null) return false;
                return Ticker == objectAsType.Ticker
                       && Date == objectAsType.Date;
            }

            public override string ToString()
            {
                return $"{Ticker}.{Date}";
            }
        }

        private void WaitForTasksToFinish(Queue<Task> tasks)
        {
            Log.Trace("TiingoNewsConverter.WaitForTasksToFinish(): start...");
            while (tasks.Count > 0)
            {
                var task = tasks.Dequeue();
                task.Wait();
            }
        }

        public void Dispose() 
        {
        }
    }
}
