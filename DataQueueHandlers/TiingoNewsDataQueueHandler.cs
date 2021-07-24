/*
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

using ProtoBuf;
using ProtoBuf.Meta;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.DataSource;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using QuantConnect.Logging;
using QuantConnect.Packets;

namespace QuantConnect.DataSource.DataQueueHandlers
{
    /// <summary>
    /// Tiingo News Data queue handler
    /// </summary>
    /// <seealso cref="IDataQueueHandler" />
    /// <seealso cref="IDisposable" />
    public class TiingoNewsDataQueueHandler : IDataQueueHandler
    {
        private readonly List<Symbol> _symbolList = new List<Symbol>();
        private readonly bool _filterTicks;
        private HashSet<Data.Custom.Tiingo.TiingoNews> _emittedNews = new HashSet<Data.Custom.Tiingo.TiingoNews>();
        private IDataAggregator _dataAggregator;
        private readonly string _tiingoToken = Config.Get("tiingo-auth-token");
        private readonly TiingoNewsComparer _comparer = new TiingoNewsComparer();
        private readonly RealTimeScheduleEventService _realTimeSchedule = new RealTimeScheduleEventService(RealTimeProvider.Instance);
        private readonly object _locker = new object();

        /// <summary>
        /// Initializes a new instance of the <see cref="TiingoNewsDataQueueHandler"/> class.
        /// </summary>
        public TiingoNewsDataQueueHandler()
        {
            RuntimeTypeModel.Default[typeof(BaseData)].AddSubType(TiingoNews.DataSourceId, typeof(TiingoNews));

            _realTimeSchedule.ScheduleEvent(TimeSpan.FromMinutes(1), DateTime.UtcNow);
            _realTimeSchedule.NewEvent += GetLatestNews;
            _dataAggregator = Composer.Instance.GetPart<IDataAggregator>() ?? 
                Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(Config.Get("data-aggregator", "QuantConnect.Data.Common.CustomDataAggregator"));

            _filterTicks = Config.GetBool("tiingo-news-filter-ticks", false);
        }

        /// <summary>
        /// Gets the latest news from Tiingo APi.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        private void GetLatestNews(object sender, EventArgs e)
        {
            var url = $"https://api.tiingo.com/tiingo/news?token={_tiingoToken}&sortBy=crawlDate".ToStringInvariant();

            try
            {
                string content;
                using (var client = new WebClient())
                {
                    content = client.DownloadString(url);
                }

                var utcNow = DateTime.UtcNow;
                var lastHourNews = JsonConvert.DeserializeObject<List<Data.Custom.Tiingo.TiingoNews>>(content, new TiingoNewsJsonConverter())
                    .Where(n => utcNow - n.CrawlDate <= TimeSpan.FromHours(1));

                _emittedNews = new HashSet<Data.Custom.Tiingo.TiingoNews>(_emittedNews.Where(n => utcNow - n.CrawlDate < TimeSpan.FromHours(3)), _comparer);

                lock (_locker)
                {
                    if (_filterTicks)
                    {
                        lastHourNews = lastHourNews.Where(n => n.Symbols.Intersect(_symbolList).Any());
                    }

                    foreach (var tiingoNews in lastHourNews.Where(n => _emittedNews.Add(n)))
                    foreach (var tiingoNewsSymbol in tiingoNews.Symbols)
                    {
                        if (_filterTicks && !_symbolList.Contains(tiingoNewsSymbol)) continue;
                        tiingoNews.Symbol = Symbol.CreateBase(typeof(Data.Custom.Tiingo.TiingoNews), tiingoNewsSymbol, Market.USA);
                        tiingoNews.Time = tiingoNews.CrawlDate;
                        _dataAggregator.Update(tiingoNews);
                    }
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception);
            }
            _realTimeSchedule.ScheduleEvent(TimeSpan.FromMinutes(1), DateTime.UtcNow);
        }

        /// <summary>
        /// Adds the specified symbols to the subscription
        /// </summary>
        /// <param name="dataConfig">defines the parameters to subscribe to a data feed</param>
        /// <param name="newDataAvailableHandler">handler to be fired on new data available</param>
        /// <returns>The new enumerator for this subscription request</returns>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            lock (_locker)
            {
                if (!_symbolList.Contains(dataConfig.Symbol))
                {
                    _symbolList.Add(dataConfig.Symbol);
                }
            }
            return _dataAggregator.Add(dataConfig, newDataAvailableHandler);
        }

        /// <summary>
        /// Removes the specified configuration
        /// </summary>
        /// <param name="dataConfig">The data config to remove</param>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            lock (_locker)
            {
                _symbolList.Remove(dataConfig.Symbol);
            }
            _dataAggregator.Remove(dataConfig);
        }

        /// <summary>
        /// Sets the job we're subscribing for
        /// </summary>
        /// <param name="job">Job we're subscribing for</param>
        public void SetJob(LiveNodePacket job)
        {
        }

        /// <summary>
        /// Returns whether the data provider is connected
        /// </summary>
        /// <returns>True if the data provider is connected</returns>
        public bool IsConnected => true;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <exception cref="NotImplementedException"></exception>
        public void Dispose()
        {
            _realTimeSchedule.NewEvent -= GetLatestNews;
            _realTimeSchedule.DisposeSafely();
        }
    }

    /// <summary>
    /// IEqualityComparer implementation for Tiingo news data.
    /// </summary>
    /// <seealso cref="TiingoNews" />
    internal class TiingoNewsComparer : IEqualityComparer<Data.Custom.Tiingo.TiingoNews>
    {
        /// <summary>
        /// Check equality.
        /// </summary>
        /// <param name="thisOne">The this one.</param>
        /// <param name="anotherOne">Another one.</param>
        /// <returns></returns>
        public bool Equals(Data.Custom.Tiingo.TiingoNews thisOne, Data.Custom.Tiingo.TiingoNews anotherOne)
        {
            return thisOne?.ArticleID == anotherOne?.ArticleID;
        }

        /// <summary>
        /// Returns a hash code for this instance.
        /// </summary>
        /// <param name="obj">The object.</param>
        /// <returns>
        /// A hash code for this instance, suitable for use in hashing algorithms and data structures like a hash table.
        /// </returns>
        public int GetHashCode(Data.Custom.Tiingo.TiingoNews obj)
        {
            return obj.ArticleID.GetHashCode();
        }
    }
}