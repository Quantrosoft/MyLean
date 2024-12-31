echo start
start cmd /c bin\debug\MyDownloader.exe --tickers "EUR_USD" --data-downloader DukascopyDataDownloader --data-type Quote --security-type Cfd --market Dukascopy --resolution "Tick" --start-date 20140101 --end-date 20300101
start cmd /c bin\debug\MyDownloader.exe --tickers "GBP_USD" --data-downloader DukascopyDataDownloader --data-type Quote --security-type Cfd --market Dukascopy --resolution "Tick" --start-date 20140101 --end-date 20300101
start cmd /c bin\debug\MyDownloader.exe --tickers "NZD_CAD" --data-downloader DukascopyDataDownloader --data-type Quote --security-type Cfd --market Dukascopy --resolution "Tick" --start-date 20140101 --end-date 20300101
start cmd /c bin\debug\MyDownloader.exe --tickers "AUD_CAD" --data-downloader DukascopyDataDownloader --data-type Quote --security-type Cfd --market Dukascopy --resolution "Tick" --start-date 20140101 --end-date 20300101
echo end
