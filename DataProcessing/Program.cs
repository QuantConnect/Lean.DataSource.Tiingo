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
using System.Globalization;
using System.IO;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Util;

namespace QuantConnect.DataProcessing
{
    /// <summary>
    /// Entrypoint for the data downloader/converter
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Entrypoint of the program
        /// </summary>
        /// <returns>Exit code. 0 equals successful, and any other value indicates the downloader/converter failed.</returns>
        public static int Main()
        {
            // Get the config values first before running. These values are set for us
            // automatically to the value set on the website when defining this data type
            var processingDateValue = Environment.GetEnvironmentVariable("QC_DATAFLEET_DEPLOYMENT_DATE");
            var processingDate = DateTime.ParseExact(processingDateValue, "yyyyMMdd", CultureInfo.InvariantCulture);
            
            var sourceDirectory = new DirectoryInfo(Config.Get("raw-folder", "/raw"));
            var destinationDirectory = new DirectoryInfo(Config.Get("temp-output-directory", "/temp-output-directory"));

            var dataProvider = Composer.Instance.GetExportedValueByTypeName<IDataProvider>(Config.Get("data-provider", "DefaultDataProvider"));
            var mapFileProvider = Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(Config.Get("map-file-provider", "LocalZipMapFileProvider"));
            mapFileProvider.Initialize(dataProvider);

            TiingoNewsConverter instance;
            try
            {
                // Pass in the values we got from the configuration into the downloader/converter.
                instance = new TiingoNewsConverter(sourceDirectory, destinationDirectory, processingDate);
            }
            catch (Exception err)
            {
                Log.Error(err, $"The {nameof(TiingoNewsConverter)} instance failed to be constructed");
                return 1;
            }

            // No need to edit anything below here for most use cases.
            // The downloader/converter is ran and cleaned up for you safely here.
            try
            {
                // Run the data downloader/converter.
                var success = instance.Convert();
                if (!success)
                {
                    Log.Error($"QuantConnect.DataProcessing.Program.Main(): Failed to process data in {nameof(TiingoNewsConverter)}");
                    return 1;
                }
            }
            catch (Exception err)
            {
                Log.Error(err, $"The {nameof(TiingoNewsConverter)} converter exited unexpectedly");
                return 1;
            }
            finally
            {
                // Run cleanup of the downloader/converter once it has finished or crashed.
                instance.DisposeSafely();
            }
            
            // The downloader/converter was successful
            return 0;
        }
    }
}
