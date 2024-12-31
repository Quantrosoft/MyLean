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
 *
*/

using NodaTime;
using QuantConnect.Util;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Configuration;
using QuantConnect.Lean.Engine.DataFeeds;
using DataFeeds = QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.DownloaderDataProvider.Launcher.Models.Constants;
using QuantConnect.Data.Market;
using QuantConnect.Data.Consolidators;

namespace QuantConnect.DownloaderDataProvider.Launcher;
public static class Program
{
    /// <summary>
    /// Synchronizer in charge of guaranteeing a single operation per file path
    /// </summary>
    private readonly static KeyStringSynchronizer DiskSynchronizer = new();

    /// <summary>
    /// The provider used to cache history data files
    /// </summary>
    private static readonly IDataCacheProvider _dataCacheProvider = new DiskDataCacheProvider(DiskSynchronizer);

    /// <summary>
    /// Represents the time interval of 5 seconds.
    /// </summary>
    private static TimeSpan _logDisplayInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Provides access to exchange hours and raw data times zones in various markets
    /// </summary>
    private static readonly MarketHoursDatabase _marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

    /// <summary>
    /// HMz: The accumulated higher timeframe quote bars
    /// </summary>
    private static List<QuoteBar>? mMinuteBarsList;
    private static List<QuoteBar>? mHourBarsList;
    private static List<QuoteBar>? mDailyBarsList;

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="args">Command-line arguments passed to the application.</param>
    public static void Main(string[] args)
    {
        // Parse report arguments and merge with config to use in the optimizer
        if (args.Length > 0)
        {
            Config.MergeCommandLineArgumentsWithConfiguration(DownloaderDataProviderArgumentParser.ParseArguments(args));
        }

        InitializeConfigurations();

        var dataDownloader = Composer.Instance.GetExportedValueByTypeName<IDataDownloader>(Config.Get(DownloaderCommandArguments.CommandDownloaderDataDownloader));

        var dataDownloadConfig = new DataDownloadConfig();

        RunDownload(dataDownloader, dataDownloadConfig, Globals.DataFolder, _dataCacheProvider);
    }

    /// <summary>
    /// Executes a data download operation using the specified data downloader.
    /// </summary>
    /// <param name="dataDownloader">An instance of an object implementing the <see cref="IDataDownloader"/> interface, responsible for downloading data.</param>
    /// <param name="dataDownloadConfig">Configuration settings for the data download operation.</param>
    /// <param name="dataDirectory">The directory where the downloaded data will be stored.</param>
    /// <param name="dataCacheProvider">The provider used to cache history data files</param>
    /// <param name="mapSymbol">True if the symbol should be mapped while writing the data</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="dataDownloader"/> is null.</exception>
    public static void RunDownload(IDataDownloader dataDownloader, DataDownloadConfig dataDownloadConfig, string dataDirectory, IDataCacheProvider dataCacheProvider, bool mapSymbol = true)
    {
        if (dataDownloader == null)
        {
            throw new ArgumentNullException(nameof(dataDownloader), "The data downloader instance cannot be null. Please ensure that a valid instance of data downloader is provided.");
        }

        var totalDownloadSymbols = dataDownloadConfig.Symbols.Count;
        var completeSymbolCount = 0;
        var startDownloadUtcTime = DateTime.UtcNow;

        foreach (var symbol in dataDownloadConfig.Symbols)
        {
            var downloadParameters = new DataDownloaderGetParameters(symbol,
                dataDownloadConfig.Resolution,
                dataDownloadConfig.StartDate,
                dataDownloadConfig.EndDate,
                dataDownloadConfig.TickType);

            Log.Trace($"DownloaderDataProvider.Main(): Starting download {downloadParameters}");

            // call market's downloader to get ticks
            var downloadedData = dataDownloader.Get(downloadParameters);

            // continue to next symbol if there are null data at the requested parameters
            if (downloadedData == null || !downloadedData.Any())
            {
                completeSymbolCount++;
                Log.Trace($"DownloaderDataProvider.Main(): No data available for the following parameters: {downloadParameters}");
                continue;
            }

            var (dataTimeZone, exchangeTimeZone) = GetDataAndExchangeTimeZoneBySymbol(symbol);
            dataTimeZone = DateTimeZone.Utc;
            exchangeTimeZone = DateTimeZone.Utc;

            // init the writers
            var tickWriter = new LeanDataWriter(dataDownloadConfig.Resolution,
                symbol,
                dataDirectory,
                dataDownloadConfig.TickType,
                dataCacheProvider,
                mapSymbol: mapSymbol);

            // write tick data
            var groupedData = DataFeeds.DownloaderDataProvider.FilterAndGroupDownloadDataBySymbol(
                downloadedData,
                symbol,
                LeanData.GetDataType(downloadParameters.Resolution, downloadParameters.TickType),
                exchangeTimeZone,
                dataTimeZone,
                downloadParameters.StartUtc,
                downloadParameters.EndUtc);
            var lastLogStatusTime = DateTime.UtcNow;
            foreach (var data in groupedData)
            {
                tickWriter.Write(data.Select(data =>
                {
                    var utcNow = DateTime.UtcNow;
                    if (utcNow - lastLogStatusTime >= _logDisplayInterval)
                    {
                        lastLogStatusTime = utcNow;
                        Log.Trace($"Downloading data for {downloadParameters.Symbol}. Please hold on...");
                    }
                    return data;
                }));
            }

            // Aggregate tick data to minute, hour, and daily QuoteBars
            var minuteBars = new List<QuoteBar>();
            var hourBars = new List<QuoteBar>();
            var dailyBars = new List<QuoteBar>();

            QuoteBar currentMinuteBar = null;
            QuoteBar currentHourBar = null;
            QuoteBar currentDailyBar = null;

            foreach (var tickData in downloadedData)
            {
                // Ensure the data is a Tick
                if (tickData is Tick tick)
                {
                    var tickTime = tick.Time.RoundDown(TimeSpan.FromMinutes(1));
                    if (currentMinuteBar == null || tickTime != currentMinuteBar.Time)
                    {
                        if (currentMinuteBar != null)
                        {
                            minuteBars.Add(currentMinuteBar);
                        }
                        currentMinuteBar = new QuoteBar
                        {
                            Time = tickTime,
                            Symbol = symbol,
                            Bid = new Bar(tick.BidPrice, tick.BidPrice, tick.BidPrice, tick.BidPrice),
                            Ask = new Bar(tick.AskPrice, tick.AskPrice, tick.AskPrice, tick.AskPrice),
                            LastBidSize = tick.BidSize,
                            LastAskSize = tick.AskSize
                        };
                    }
                    else
                    {
                        if (tick.BidPrice > 0)
                        {
                            currentMinuteBar.Bid.High = Math.Max(currentMinuteBar.Bid.High, tick.BidPrice);
                            currentMinuteBar.Bid.Low = Math.Min(currentMinuteBar.Bid.Low, tick.BidPrice);
                            currentMinuteBar.Bid.Close = tick.BidPrice;
                        }

                        if (tick.AskPrice > 0)
                        {
                            currentMinuteBar.Ask.High = Math.Max(currentMinuteBar.Ask.High, tick.AskPrice);
                            currentMinuteBar.Ask.Low = Math.Min(currentMinuteBar.Ask.Low, tick.AskPrice);
                            currentMinuteBar.Ask.Close = tick.AskPrice;
                        }
                    }

                    var hourTime = tick.Time.RoundDown(TimeSpan.FromHours(1));
                    if (currentHourBar == null || hourTime != currentHourBar.Time)
                    {
                        if (currentHourBar != null)
                        {
                            hourBars.Add(currentHourBar);
                        }
                        currentHourBar = new QuoteBar
                        {
                            Time = hourTime,
                            Symbol = symbol,
                            Bid = new Bar(tick.BidPrice, tick.BidPrice, tick.BidPrice, tick.BidPrice),
                            Ask = new Bar(tick.AskPrice, tick.AskPrice, tick.AskPrice, tick.AskPrice),
                            LastBidSize = tick.BidSize,
                            LastAskSize = tick.AskSize
                        };
                    }
                    else
                    {
                        if (tick.BidPrice > 0)
                        {
                            currentHourBar.Bid.High = Math.Max(currentHourBar.Bid.High, tick.BidPrice);
                            currentHourBar.Bid.Low = Math.Min(currentHourBar.Bid.Low, tick.BidPrice);
                            currentHourBar.Bid.Close = tick.BidPrice;
                        }

                        if (tick.AskPrice > 0)
                        {
                            currentHourBar.Ask.High = Math.Max(currentHourBar.Ask.High, tick.AskPrice);
                            currentHourBar.Ask.Low = Math.Min(currentHourBar.Ask.Low, tick.AskPrice);
                            currentHourBar.Ask.Close = tick.AskPrice;
                        }
                    }

                    var dailyTime = tick.Time.Date;
                    if (currentDailyBar == null || dailyTime != currentDailyBar.Time)
                    {
                        if (currentDailyBar != null)
                        {
                            dailyBars.Add(currentDailyBar);
                        }
                        currentDailyBar = new QuoteBar
                        {
                            Time = dailyTime,
                            Symbol = symbol,
                            Bid = new Bar(tick.BidPrice, tick.BidPrice, tick.BidPrice, tick.BidPrice),
                            Ask = new Bar(tick.AskPrice, tick.AskPrice, tick.AskPrice, tick.AskPrice),
                            LastBidSize = tick.BidSize,
                            LastAskSize = tick.AskSize
                        };
                    }
                    else
                    {
                        if (tick.BidPrice > 0)
                        {
                            currentDailyBar.Bid.High = Math.Max(currentDailyBar.Bid.High, tick.BidPrice);
                            currentDailyBar.Bid.Low = Math.Min(currentDailyBar.Bid.Low, tick.BidPrice);
                            currentDailyBar.Bid.Close = tick.BidPrice;
                        }

                        if (tick.AskPrice > 0)
                        {
                            currentDailyBar.Ask.High = Math.Max(currentDailyBar.Ask.High, tick.AskPrice);
                            currentDailyBar.Ask.Low = Math.Min(currentDailyBar.Ask.Low, tick.AskPrice);
                            currentDailyBar.Ask.Close = tick.AskPrice;
                        }
                    }
                }
            }

            if (currentMinuteBar != null)
                minuteBars.Add(currentMinuteBar);
            if (currentHourBar != null)
                hourBars.Add(currentHourBar);
            if (currentDailyBar != null)
                dailyBars.Add(currentDailyBar);

            // Write aggregated QuoteBars
            var minuteWriter = new LeanDataWriter(Resolution.Minute, symbol, dataDirectory,
                TickType.Quote, dataCacheProvider, writePolicy: WritePolicy.Merge);
            minuteWriter.Write(minuteBars);

            var hourWriter = new LeanDataWriter(Resolution.Hour, symbol, dataDirectory,
                TickType.Quote, dataCacheProvider, writePolicy: WritePolicy.Merge);
            hourWriter.Write(hourBars);

            var dailyWriter = new LeanDataWriter(Resolution.Daily, symbol, dataDirectory,
                TickType.Quote, dataCacheProvider, writePolicy: WritePolicy.Merge);
            dailyWriter.Write(dailyBars);

            completeSymbolCount++;
            var symbolPercentComplete = (double)completeSymbolCount / totalDownloadSymbols * 100;
            Log.Trace($"DownloaderDataProvider.RunDownload(): {symbolPercentComplete:F2}% complete ({completeSymbolCount} out of {totalDownloadSymbols} symbols)");

            Log.Trace($"DownloaderDataProvider.RunDownload(): Download completed for {downloadParameters.Symbol} at {downloadParameters.Resolution} resolution, " +
                $"covering the period from {dataDownloadConfig.StartDate} to {dataDownloadConfig.EndDate}.");
        }

        Log.Trace($"All downloads completed in {(DateTime.UtcNow - startDownloadUtcTime).TotalSeconds:F2} seconds.");
    }


    /// <summary>
    /// Retrieves the data time zone and exchange time zone associated with the specified symbol.
    /// </summary>
    /// <param name="symbol">The symbol for which to retrieve time zones.</param>
    /// <returns>
    /// A tuple containing the data time zone and exchange time zone.
    /// The data time zone represents the time zone for data related to the symbol.
    /// The exchange time zone represents the time zone for trading activities related to the symbol.
    /// </returns>
    private static (DateTimeZone dataTimeZone, DateTimeZone exchangeTimeZone) GetDataAndExchangeTimeZoneBySymbol(Symbol symbol)
    {
        var entry = _marketHoursDatabase.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType);
        return (entry.DataTimeZone, entry.ExchangeHours.TimeZone);
    }

    /// <summary>
    /// Initializes various configurations for the application.
    /// This method sets up logging, data providers, map file providers, and factor file providers.
    /// </summary>
    /// <remarks>
    /// The method reads configuration values to determine whether debugging is enabled, 
    /// which log handler to use, and which data, map file, and factor file providers to initialize.
    /// </remarks>
    /// <seealso cref="Log"/>
    /// <seealso cref="Config"/>
    /// <seealso cref="Composer"/>
    /// <seealso cref="ILogHandler"/>
    /// <seealso cref="IDataProvider"/>
    /// <seealso cref="IMapFileProvider"/>
    /// <seealso cref="IFactorFileProvider"/>
    public static void InitializeConfigurations()
    {
        Log.DebuggingEnabled = Config.GetBool("debug-mode", false);
        Log.LogHandler = Composer.Instance.GetExportedValueByTypeName<ILogHandler>(Config.Get("log-handler", "CompositeLogHandler"));

        var dataProvider = Composer.Instance.GetExportedValueByTypeName<IDataProvider>("DefaultDataProvider");
        var mapFileProvider = Composer.Instance.GetExportedValueByTypeName<IMapFileProvider>(Config.Get("map-file-provider", "LocalDiskMapFileProvider"));
        var factorFileProvider = Composer.Instance.GetExportedValueByTypeName<IFactorFileProvider>(Config.Get("factor-file-provider", "LocalDiskFactorFileProvider"));

        var optionChainProvider = Composer.Instance.GetPart<IOptionChainProvider>();
        if (optionChainProvider == null)
        {
            optionChainProvider = new CachingOptionChainProvider(new LiveOptionChainProvider(new ZipDataCacheProvider(dataProvider, false), mapFileProvider));
            Composer.Instance.AddPart(optionChainProvider);
        }

        mapFileProvider.Initialize(dataProvider);
        factorFileProvider.Initialize(mapFileProvider, dataProvider);
    }
}
