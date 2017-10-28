﻿using Binance;
using Binance.Account;
using Binance.Account.Orders;
using Binance.Api;
using Binance.Api.WebSocket;
using Binance.Api.WebSocket.Events;
using Binance.Cache;
using Binance.Cache.Events;
using Binance.Market;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace BinanceConsoleApp
{
    /// <summary>
    /// .NET Core console application used for Binance integration testing.
    /// </summary>
    class Program
    {
        private static IConfigurationRoot _configuration;

        private static IServiceProvider _serviceProvider;

        private static IBinanceApi _api;
        private static IBinanceUser _user;

        private static IOrderBookCache _orderBookCache;
        private static ICandlesticksCache _klineCache;
        private static IAggregateTradesCache _tradesCache;
        private static IUserDataWebSocketClient _userDataClient;

        private static Task _liveTask;
        private static CancellationTokenSource _liveTokenSource;

        private static readonly object _consoleSync = new object();

        private static bool _isOrdersTestOnly = true;

        public static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();

            try
            {
                // Load configuration.
                _configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .AddUserSecrets<Program>()
                    .Build();

                // Configure services.
               _serviceProvider = new ServiceCollection()
                    .AddBinance().AddLogging().AddOptions()
                    .Configure<BinanceJsonApiOptions>(_configuration.GetSection("Api"))
                    .Configure<UserDataWebSocketClientOptions>(_configuration.GetSection("UserClient"))
                    .BuildServiceProvider();

                // Configure logging.
                _serviceProvider
                    .GetService<ILoggerFactory>()
                        .AddConsole(_configuration.GetSection("Logging.Console"));

                var key = _configuration["BinanceApiKey"] // user secrets configuration.
                    ?? _configuration.GetSection("User")["ApiKey"]; // appsettings.json configuration.

                var secret = _configuration["BinanceApiSecret"] // user secrets configuration.
                    ?? _configuration.GetSection("User")["ApiSecret"]; // appsettings.json configuration.

                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
                {
                    PrintApiNotice();
                }

                if (!string.IsNullOrEmpty(key))
                {
                    _user = new BinanceUser(key, secret);
                }

                _api = _serviceProvider.GetService<IBinanceApi>();

                await SuperLoopAsync(cts.Token);
            }
            catch (Exception e)
            {
                lock (_consoleSync)
                {
                    Console.WriteLine($"! FAIL: \"{e.Message}\"");
                    if (e.InnerException != null)
                        Console.WriteLine($"  -> Exception: \"{e.InnerException.Message}\"");
                }
            }
            finally
            {
                await DisableLiveTask();

                cts?.Cancel();
                cts?.Dispose();

                _api?.Dispose();
                _user?.Dispose();

                lock (_consoleSync)
                {
                    Console.WriteLine();
                    Console.WriteLine("  ...press any key to close window.");
                    Console.ReadKey(true);
                }
            }
        }

        private static void PrintHelp()
        {
            lock (_consoleSync)
            {
                Console.WriteLine();
                Console.WriteLine("Usage: <command> <args>");
                Console.WriteLine();
                Console.WriteLine("Commands:");
                Console.WriteLine();
                Console.WriteLine(" Connectivity:");
                Console.WriteLine("  ping                                                 test connection to server.");
                Console.WriteLine("  time                                                 display the current server time (UTC).");
                Console.WriteLine();
                Console.WriteLine(" Market Data:");
                Console.WriteLine("  stats <symbol>                                       display 24h stats for symbol.");
                Console.WriteLine("  depth|book <symbol> [limit]                          display symbol order book, where limit: [1-100].");
                Console.WriteLine("  trades <symbol> [limit]                              display latest trades, where limit: [1-500].");
                Console.WriteLine("  tradesIn <symbol> <start> <end>                      display trades within a time range (inclusive).");
                Console.WriteLine("  tradesFrom <symbol> <tradeId> [limit]                display trades beginning with trade ID.");
                Console.WriteLine("  candles|klines <symbol> <interval> [limit]           display candlestick bars for a symbol.");
                Console.WriteLine("  candlesIn|klinesIn <symbol> <interval> <start> <end> display candlestick bars for a symbol in time range.");
                Console.WriteLine("  symbols                                              display all symbols.");
                Console.WriteLine("  prices                                               display current price for all symbols.");
                Console.WriteLine("  tops                                                 display order book top price and quantity for all symbols.");
                Console.WriteLine("  live depth|book <symbol>                            enable order book live feed for a symbol.");
                Console.WriteLine("  live kline|candle <symbol> <interval>                enable kline live feed for a symbol and interval.");
                Console.WriteLine("  live trades <symbol>                                 enable trades live feed for a symbol.");
                Console.WriteLine("  live account|user                                    enable user data live feed (api key required).");
                Console.WriteLine("  live off                                             disable the websocket live feed (there can be only one).");
                Console.WriteLine();
                Console.WriteLine(" Account (authentication required):");
                Console.WriteLine("  market <side> <symbol> <qty> [stop]                  create a market order.");
                Console.WriteLine("  limit <side> <symbol> <qty> <price> [stop]           create a limit order.");
                Console.WriteLine("  orders <symbol> [limit]                              display orders for a symbol, where limit: [1-500].");
                Console.WriteLine("  orders <symbol> open                                 display all open orders for a symbol.");
                Console.WriteLine("  order <symbol> <ID>                                  display an order by ID.");
                Console.WriteLine("  order <symbol> <ID> cancel                           cancel an order by ID.");
                Console.WriteLine("  account|balances|positions                           display user account information (including balances).");
                Console.WriteLine("  myTrades <symbol> [limit]                            display user trades of a symbol.");
                Console.WriteLine("  deposits [asset]                                     display user deposits of an asset or all deposits.");
                Console.WriteLine("  withdrawals [asset]                                  display user withdrawals of an asset or all withdrawals.");
                Console.WriteLine("  withdraw <asset> <address> <amount>                  submit a withdraw request (NOTE: 'test only' does NOT apply).");
                Console.WriteLine("  test <on|off>                                        determines if orders are test only (default: on).");
                Console.WriteLine();
                Console.WriteLine("  quit | exit                                          terminate the application.");
                Console.WriteLine();
                Console.WriteLine(" * default symbol: BTCUSDT");
                Console.WriteLine(" * default limit: 10");
                Console.WriteLine();
            }
        }

        private static void PrintApiNotice()
        {
            lock (_consoleSync)
            {
                Console.WriteLine("* NOTICE: To access some Binance endpoint features, your API Key and Secret may be required.");
                Console.WriteLine();
                Console.WriteLine("  You can either modify the 'ApiKey' and 'ApiSecret' configuration values in appsettings.json.");
                Console.WriteLine();
                Console.WriteLine("  Or use the following commands to configure the .NET user secrets for the project:");
                Console.WriteLine();
                Console.WriteLine("    dotnet user-secrets set BinanceApiKey <your api key>");
                Console.WriteLine("    dotnet user-secrets set BinanceApiSecret <your api secret>");
                Console.WriteLine();
                Console.WriteLine("  For more information: https://docs.microsoft.com/en-us/aspnet/core/security/app-secrets");
                Console.WriteLine();
            }
        }

        private static async Task SuperLoopAsync(CancellationToken token = default)
        {
            PrintHelp();

            do
            {
                try
                {
                    var stdin = Console.ReadLine()?.Trim();
                    if (string.IsNullOrWhiteSpace(stdin))
                    {
                        PrintHelp();
                        continue;
                    }

                    // Quit/Exit
                    if (stdin.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                        stdin.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    // Ping
                    else if (stdin.Equals("ping", StringComparison.OrdinalIgnoreCase))
                    {
                        var isSuccessful = await _api.PingAsync(token);
                        lock (_consoleSync)
                        {
                            Console.WriteLine($"  Ping: {(isSuccessful ? "SUCCESSFUL" : "FAILED")}");
                            Console.WriteLine();
                        }
                    }
                    // Time
                    else if (stdin.Equals("time", StringComparison.OrdinalIgnoreCase))
                    {
                        var time = await _api.GetTimeAsync(token);
                        lock (_consoleSync)
                        {
                            Console.WriteLine($"  {time.Kind.ToString().ToUpper()} Time: {time}  [Local: {time.ToLocalTime()}]");
                            Console.WriteLine();
                        }
                    }
                    // Stats (24-hour)
                    else if (stdin.StartsWith("stats", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        var stats = await _api.Get24hrStatsAsync(symbol, token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  24-hour statistics for {stats.Symbol}:");
                            Console.WriteLine($"    %: {stats.PriceChangePercent.ToString("0.00")} | O: {stats.OpenPrice.ToString("0.00000000")} | H: {stats.HighPrice.ToString("0.00000000")} | L: {stats.LowPrice.ToString("0.00000000")} | V: {stats.Volume.ToString("0.")}");
                            Console.WriteLine($"    Bid: {stats.BidPrice.ToString("0.00000000")} | Last: {stats.LastPrice.ToString("0.00000000")} | Ask: {stats.AskPrice.ToString("0.00000000")} | Avg: {stats.WeightedAveragePrice.ToString("0.00000000")}");
                            Console.WriteLine();
                        }
                    }
                    // Order Book
                    else if (stdin.StartsWith("depth", StringComparison.OrdinalIgnoreCase)
                          || stdin.StartsWith("book", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        int limit = 10;

                        if (args.Length > 1)
                        {
                            if (!int.TryParse(args[1], out limit))
                            {
                                symbol = args[1];
                                limit = 10;
                            }
                        }

                        if (args.Length > 2)
                        {
                            int.TryParse(args[2], out limit);
                        }

                        OrderBook orderBook = null;

                        // If live order book is active (for symbol), get cached data.
                        if (_orderBookCache != null && _orderBookCache.OrderBook.Symbol == symbol)
                            orderBook = _orderBookCache.OrderBook; // get local cache.

                        // Query order book from API, if needed.
                        if (orderBook == null)
                            orderBook = await _api.GetOrderBookAsync(symbol, limit, token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            orderBook.Print(Console.Out, limit);
                            Console.WriteLine();
                        }
                    }
                    // Trades from ID
                    else if (stdin.StartsWith("tradesFrom", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        long fromId = 0;
                        if (args.Length > 2)
                        {
                            long.TryParse(args[2], out fromId);
                        }

                        int limit = 10;
                        if (args.Length > 3)
                        {
                            int.TryParse(args[3], out limit);
                        }

                        var trades = await _api.GetAggregateTradesAsync(symbol, fromId: fromId, limit: limit, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var trade in trades)
                            {
                                Display(trade);
                            }
                            Console.WriteLine();
                        }
                    }
                    // Trades within time range
                    else if (stdin.StartsWith("tradesIn", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        long startTime = 0;
                        if (args.Length > 2)
                        {
                            long.TryParse(args[2], out startTime);
                        }

                        long endTime = 0;
                        if (args.Length > 3)
                        {
                            long.TryParse(args[3], out endTime);
                        }

                        var trades = await _api.GetAggregateTradesAsync(symbol, startTime: startTime, endTime: endTime, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var trade in trades)
                            {
                                Display(trade);
                            }
                            Console.WriteLine();
                        }
                    }
                    // Trades
                    else if (stdin.StartsWith("trades", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        int limit = 10;
                        if (args.Length > 2)
                        {
                            int.TryParse(args[2], out limit);
                        }

                        IEnumerable<AggregateTrade> trades = null;

                        // If live order book is active (for symbol), get cached data.
                        if (_tradesCache != null && _tradesCache.Trades.FirstOrDefault()?.Symbol == symbol)
                            trades = _tradesCache.Trades.Reverse().Take(limit); // get local cache.

                        if (trades == null)
                            trades = (await _api.GetAggregateTradesAsync(symbol, limit: limit, token: token)).Reverse();

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var trade in trades)
                            {
                                Display(trade);
                            }
                            Console.WriteLine();
                        }
                    }
                    // Candlesticks within time range
                    else if (stdin.StartsWith("candlesIn", StringComparison.OrdinalIgnoreCase)
                          || stdin.StartsWith("klinesIn", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        var interval = KlineInterval.Hour;
                        if (args.Length > 2)
                        {
                            interval = args[2].ToKlineInterval();
                        }

                        long startTime = 0;
                        if (args.Length > 3)
                        {
                            long.TryParse(args[3], out startTime);
                        }

                        long endTime = 0;
                        if (args.Length > 4)
                        {
                            long.TryParse(args[4], out endTime);
                        }

                        var candlesticks = await _api.GetCandlesticksAsync(symbol, interval, startTime: startTime, endTime: endTime, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var candlestick in candlesticks)
                            {
                                Display(candlestick);
                            }
                            Console.WriteLine();
                        }
                    }
                    // Candlesticks
                    else if (stdin.StartsWith("candles", StringComparison.OrdinalIgnoreCase)
                          || stdin.StartsWith("klines", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 1)
                        {
                            symbol = args[1];
                        }

                        var interval = KlineInterval.Hour;
                        if (args.Length > 2)
                        {
                            interval = args[2].ToKlineInterval();
                        }

                        int limit = 10;
                        if (args.Length > 3)
                        {
                            int.TryParse(args[3], out limit);
                        }

                        IEnumerable<Candlestick> candlesticks = null;

                        // If live order book is active (for symbol), get cached data.
                        if (_klineCache != null && _klineCache.Candlesticks.FirstOrDefault()?.Symbol == symbol)
                            candlesticks = _klineCache.Candlesticks.Reverse().Take(limit); // get local cache.

                        if (candlesticks == null)
                            candlesticks = await _api.GetCandlesticksAsync(symbol, interval, limit, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var candlestick in candlesticks)
                            {
                                Display(candlestick);
                            }
                            Console.WriteLine();
                        }
                    }
                    // Symbols
                    else if (stdin.Equals("symbols", StringComparison.OrdinalIgnoreCase))
                    {
                        var symbols = await _api.SymbolsAsync(token);
                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            Console.WriteLine(string.Join(", ", symbols));
                            Console.WriteLine();
                        }
                    }
                    // Prices
                    else if (stdin.Equals("prices", StringComparison.OrdinalIgnoreCase))
                    {
                        var prices = await _api.GetPricesAsync(token);
                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var price in prices)
                            {
                                Console.WriteLine($"  {price.Symbol.PadLeft(8)}: {price.Value}");
                            }
                            Console.WriteLine();
                        }
                    }
                    // Tops
                    else if (stdin.Equals("tops", StringComparison.OrdinalIgnoreCase))
                    {
                        var tops = await _api.GetOrderBookTopsAsync(token);
                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            foreach (var top in tops)
                            {
                                Console.WriteLine($"  {top.Symbol.PadLeft(8)}  -  Bid: {top.Bid.Price.ToString().PadLeft(12)} (qty: {top.Bid.Quantity})  |  Ask: {top.Ask.Price} (qty: {top.Ask.Quantity})");
                            }
                            Console.WriteLine();
                        }
                    }
                    // Live feeds
                    else if (stdin.StartsWith("live", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string endpoint = "depth";
                        if (args.Length > 1)
                        {
                            endpoint = args[1];
                        }

                        string symbol = Symbol.BTC_USDT;
                        if (args.Length > 2)
                        {
                            symbol = args[2];
                        }

                        if (endpoint.Equals("depth", StringComparison.OrdinalIgnoreCase)
                            || endpoint.Equals("book", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_liveTask != null)
                            {
                                lock (_consoleSync)
                                {
                                    Console.WriteLine($"! A live task is currently active ...use 'live off' to disable.");
                                }
                                continue;
                            }

                            _liveTokenSource = new CancellationTokenSource();

                            _orderBookCache = _serviceProvider.GetService<IOrderBookCache>();
                            _orderBookCache.Update += OnOrderBookUpdated;

                            _liveTask = Task.Run(() => _orderBookCache.SubscribeAsync(symbol, token: _liveTokenSource.Token));

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"  ...live order book enabled for symbol: {symbol} ...use 'live off' to disable.");
                            }
                        }
                        else if (endpoint.Equals("kline", StringComparison.OrdinalIgnoreCase)
                              || endpoint.Equals("candle", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_liveTask != null)
                            {
                                lock (_consoleSync)
                                {
                                    Console.WriteLine($"! A live task is currently active ...use 'live off' to disable.");
                                }
                                continue;
                            }

                            var interval = KlineInterval.Hour;
                            if (args.Length > 3)
                            {
                                interval = args[3].ToKlineInterval();
                            }

                            _liveTokenSource = new CancellationTokenSource();

                            _klineCache = _serviceProvider.GetService<ICandlesticksCache>();
                            _klineCache.Client.Kline += OnKlineEvent;

                            _liveTask = Task.Run(() => _klineCache.SubscribeAsync(symbol, interval, (e) => { Display(e.Candlesticks.Last()); }, token: _liveTokenSource.Token));

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"  ...live kline feed enabled for symbol: {symbol}, interval: {interval} ...use 'live off' to disable.");
                            }
                        }
                        else if (endpoint.Equals("trades", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_liveTask != null)
                            {
                                lock (_consoleSync)
                                {
                                    Console.WriteLine($"! A live task is currently active ...use 'live off' to disable.");
                                }
                                continue;
                            }

                            _liveTokenSource = new CancellationTokenSource();

                            _tradesCache = _serviceProvider.GetService<IAggregateTradesCache>();

                            _liveTask = Task.Run(() => _tradesCache.SubscribeAsync(symbol, (e) => { Display(e.LatestTrade()); }, token: _liveTokenSource.Token));

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"  ...live trades feed enabled for symbol: {symbol} ...use 'live off' to disable.");
                            }
                        }
                        else if (endpoint.Equals("account", StringComparison.OrdinalIgnoreCase)
                              || endpoint.Equals("user", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_liveTask != null)
                            {
                                lock (_consoleSync)
                                {
                                    Console.WriteLine($"! A live task is currently active ...use 'live off' to disable.");
                                }
                                continue;
                            }

                            if (_user == null)
                            {
                                PrintApiNotice();
                                continue;
                            }

                            _liveTokenSource = new CancellationTokenSource();

                            _userDataClient = _serviceProvider.GetService<IUserDataWebSocketClient>();
                            _userDataClient.AccountUpdate += OnAccountUpdateEvent;
                            _userDataClient.OrderUpdate += OnOrderUpdateEvent;
                            _userDataClient.TradeUpdate += OnTradeUpdateEvent;

                            _liveTask = Task.Run(() => _userDataClient.SubscribeAsync(_user, _liveTokenSource.Token));

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"  ...live account feed enabled ...use 'live off' to disable.");
                            }
                        }
                        else if (endpoint.Equals("off", StringComparison.OrdinalIgnoreCase))
                        {
                            await DisableLiveTask();
                        }
                        else
                        {
                            lock (_consoleSync)
                            {
                                Console.WriteLine($"! Unrecognized Command: \"{stdin}\"");
                                PrintHelp();
                            }
                            continue;
                        }
                    }
                    // Market order
                    else if (stdin.StartsWith("market", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        if (args.Length < 4)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A side, symbol, and quantity are required.");
                            continue;
                        }

                        if (!Enum.TryParse(typeof(OrderSide), args[1], true, out var side))
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A valid order side is required ('buy' or 'sell').");
                            continue;
                        }

                        var symbol = args[2];

                        if (!decimal.TryParse(args[3], out var quantity) || quantity <= 0)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A quantity greater than 0 is required.");
                            continue;
                        }

                        decimal stopPrice = 0;
                        if (args.Length > 4)
                        {
                            if (!decimal.TryParse(args[4], out stopPrice) || stopPrice <= 0)
                            {
                                lock (_consoleSync)
                                    Console.WriteLine("A stop price greater than 0 is required.");
                                continue;
                            }
                        }

                        var clientOrder = new MarketOrder()
                        {
                            Symbol = symbol,
                            Side = (OrderSide)side,
                            Quantity = quantity,
                            StopPrice = stopPrice,
                            IsTestOnly = _isOrdersTestOnly // *** NOTICE *** 
                        };

                        var order = await _api.PlaceAsync(_user, clientOrder, token: token);

                        if (order != null)
                        {
                            lock (_consoleSync)
                            {
                                Console.WriteLine($"{(clientOrder.IsTestOnly ? "~ TEST ~ " : "")}>> MARKET {order.Side} order (ID: {order.Id}) placed for {order.OriginalQuantity.ToString("0.00000000")} {order.Symbol} @ {order.Price.ToString("0.00000000")}.");
                            }
                        }
                    }
                    // Limit order
                    else if (stdin.StartsWith("limit", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        if (args.Length < 5)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A side, symbol, quantity and price are required.");
                            continue;
                        }

                        if (!Enum.TryParse(typeof(OrderSide), args[1], true, out var side))
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A valid order side is required ('buy' or 'sell').");
                            continue;
                        }

                        var symbol = args[2];

                        if (!decimal.TryParse(args[3], out var quantity) || quantity <= 0)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A quantity greater than 0 is required.");
                            continue;
                        }

                        if (!decimal.TryParse(args[4], out var price) || price <= 0)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("A price greater than 0 is required.");
                            continue;
                        }

                        decimal stopPrice = 0;
                        if (args.Length > 5)
                        {
                            if (!decimal.TryParse(args[5], out stopPrice) || stopPrice <= 0)
                            {
                                lock (_consoleSync)
                                    Console.WriteLine("A stop price greater than 0 is required.");
                                continue;
                            }
                        }

                        var clientOrder = new LimitOrder()
                        {
                            Symbol = symbol,
                            Side = (OrderSide)side,
                            Quantity = quantity,
                            Price = price,
                            StopPrice = stopPrice,
                            IsTestOnly = _isOrdersTestOnly // *** NOTICE *** 
                        };

                        var order = await _api.PlaceAsync(_user, clientOrder, token: token);

                        if (order != null)
                        {
                            lock (_consoleSync)
                            {
                                Console.WriteLine($"{(clientOrder.IsTestOnly ? "TEST " : "")}>> LIMIT {order.Side} order (ID: {order.Id}) placed for {order.OriginalQuantity.ToString("0.00000000")} {order.Symbol} @ {order.Price.ToString("0.00000000")}.");
                            }
                        }
                    }
                    // Orders
                    else if (stdin.StartsWith("orders", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        bool openOrders = false;
                        int limit = 10;

                        if (args.Length > 1)
                        {
                            if (!int.TryParse(args[1], out limit))
                            {
                                symbol = args[1];
                                limit = 10;
                            }
                        }

                        if (args.Length > 2)
                        {
                            if (!int.TryParse(args[2], out limit))
                            {
                                if (args[2].Equals("open", StringComparison.OrdinalIgnoreCase))
                                    openOrders = true;

                                limit = 10;
                            }
                        }

                        var orders = openOrders
                            ? await _api.GetOpenOrdersAsync(_user, symbol, token: token)
                            : await _api.GetOrdersAsync(_user, symbol, limit: limit, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            if (!orders.Any())
                            {
                                Console.WriteLine("[None]");
                            }
                            else
                            {
                                foreach (var order in orders)
                                {
                                    Display(order);
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                    // Order
                    else if (stdin.StartsWith("order", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var args = stdin.Split(' ');

                        if (args.Length < 3)
                        {
                            Console.WriteLine("A symbol and order ID are required.");
                            continue;
                        }

                        var symbol = args[1];

                        string clientOrderId = null;

                        if (!long.TryParse(args[2], out var id))
                        {
                            clientOrderId = args[2];
                        }
                        else if (id < 0)
                        {
                            Console.WriteLine("An order ID not less than 0 is required.");
                            continue;
                        }

                        if (args.Length > 3 && args[3].Equals("cancel", StringComparison.OrdinalIgnoreCase))
                        {
                            var cancelOrderId = clientOrderId != null
                               ? await _api.CancelOrderAsync(_user, symbol, clientOrderId, token: token)
                               : await _api.CancelOrderAsync(_user, symbol, id, token: token);

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                Console.WriteLine($"Cancel Order ID: {cancelOrderId}");
                                Console.WriteLine();
                            }
                        }
                        else
                        {
                            var order = clientOrderId != null
                                ? await _api.GetOrderAsync(_user, symbol, clientOrderId, token: token)
                                : await _api.GetOrderAsync(_user, symbol, id, token: token);

                            lock (_consoleSync)
                            {
                                Console.WriteLine();
                                if (order == null)
                                {
                                    Console.WriteLine("[Not Found]");
                                }
                                else
                                {
                                    Display(order);
                                }
                                Console.WriteLine();
                            }
                        }
                    }
                    // Account
                    else if (stdin.Equals("account", StringComparison.OrdinalIgnoreCase)
                          || stdin.Equals("balances", StringComparison.OrdinalIgnoreCase)
                          || stdin.Equals("positions", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var account = await _api.GetAccountAsync(_user, token: token);

                        Display(account);
                    }
                    // My Trades
                    else if (stdin.StartsWith("mytrades", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var args = stdin.Split(' ');

                        string symbol = Symbol.BTC_USDT;
                        int limit = 10;

                        if (args.Length > 1)
                        {
                            if (!int.TryParse(args[1], out limit))
                            {
                                symbol = args[1];
                                limit = 10;
                            }
                        }

                        if (args.Length > 2)
                        {
                            if (!int.TryParse(args[2], out limit))
                            {
                                limit = 10;
                            }
                        }

                        var trades = await _api.GetTradesAsync(_user, symbol, limit: limit, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            if (!trades.Any())
                            {
                                Console.WriteLine("[None]");
                            }
                            else
                            {
                                foreach (var trade in trades)
                                {
                                    Display(trade);
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                    // Deposits
                    else if (stdin.StartsWith("deposits", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var args = stdin.Split(' ');

                        string asset = null;
                        if (args.Length > 1)
                        {
                            asset = args[1];
                        }

                        var deposits = await _api.GetDepositsAsync(_user, asset, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            if (!deposits.Any())
                            {
                                Console.WriteLine("[None]");
                            }
                            else
                            {
                                foreach (var deposit in deposits)
                                {
                                    Console.WriteLine($"  {deposit.Time().ToLocalTime()} - {deposit.Asset.PadLeft(4)} - {deposit.Amount.ToString("0.00000000")} - Status: {deposit.Status}");
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                    // Withdrawals
                    else if (stdin.StartsWith("withdrawals", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_user == null)
                        {
                            PrintApiNotice();
                            continue;
                        }

                        var args = stdin.Split(' ');

                        string asset = null;
                        if (args.Length > 1)
                        {
                            asset = args[1];
                        }

                        var withdrawals = await _api.GetWithdrawalsAsync(_user, asset, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            if (!withdrawals.Any())
                            {
                                Console.WriteLine("[None]");
                            }
                            else
                            {
                                foreach (var withdrawal in withdrawals)
                                {
                                    Console.WriteLine($"  {withdrawal.Time().ToLocalTime()} - {withdrawal.Asset.PadLeft(4)} - {withdrawal.Amount.ToString("0.00000000")} => {withdrawal.Address} - Status: {withdrawal.Status}");
                                }
                            }
                            Console.WriteLine();
                        }
                    }
                    // Withdraw
                    else if (stdin.StartsWith("withdraw", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        if (args.Length < 4)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("An asset, address, and amount are required.");
                            continue;
                        }

                        var asset = args[1];

                        var address = args[2];

                        if (!decimal.TryParse(args[3], out var amount) || amount <= 0)
                        {
                            lock (_consoleSync)
                                Console.WriteLine("An amount greater than 0 is required.");
                            continue;
                        }

                        await _api.WithdrawAsync(_user, asset, address, amount, token: token);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  Withdraw request successful: {amount} {asset} => {address}");
                        }
                    }
                    // Test-Only Orders (enable/disable)
                    else if (stdin.StartsWith("test", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        string value = "on";

                        if (args.Length > 1)
                        {
                            value = args[1];
                        }

                        _isOrdersTestOnly = !value.Equals("off", StringComparison.OrdinalIgnoreCase);

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  Test orders: {(_isOrdersTestOnly ? "ON" : "OFF")}");
                            if (!_isOrdersTestOnly)
                                Console.WriteLine($"  !! Market and Limit orders WILL be placed !!");
                            Console.WriteLine();
                        }
                    }
                    // Debug
                    else if (stdin.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
                    {
                        var args = stdin.Split(' ');

                        // ...for development testing only...

                        lock (_consoleSync)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"  Done.");
                            Console.WriteLine();
                        }
                    }
                    else
                    {
                        lock (_consoleSync)
                        {
                            Console.WriteLine($"! Unrecognized Command: \"{stdin}\"");
                            PrintHelp();
                        }
                        continue;
                    }
                }
                catch (Exception e)
                {
                    lock (_consoleSync)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"! Exception: {e.Message}");
                        if (e.InnerException != null)
                        {
                            Console.WriteLine($"  -> {e.InnerException.Message}");
                        }
                    }
                }
            }
            while (true);
        }

        private static async Task DisableLiveTask()
        {
            _liveTokenSource?.Cancel();

            // Wait for live task to complete.
            if (_liveTask != null && !_liveTask.IsCompleted)
                await _liveTask;

            _orderBookCache?.Dispose();
            _tradesCache?.Dispose();
            _klineCache?.Dispose();
            _userDataClient?.Dispose();

            _liveTokenSource?.Dispose();

            if (_orderBookCache != null)
            {
                lock (_consoleSync) 
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ...live order book feed disabled.");
                }
            }
            _orderBookCache = null;

            if (_klineCache != null)
            {
                lock (_consoleSync)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ...live kline feed disabled.");
                }
            }
            _klineCache = null;

            if (_tradesCache != null)
            {
                lock (_consoleSync)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ...live trades feed disabled.");
                }
            }
            _tradesCache = null;

            if (_userDataClient != null)
            {
                lock (_consoleSync)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  ...live account feed disabled.");
                }
            }
            _userDataClient = null;

            _liveTokenSource = null;
            _liveTask = null;
        }

        private static void OnOrderBookUpdated(object sender, OrderBookCacheEventArgs e)
        {
            // NOTE: object 'sender' is IOrderBookCache (live order book)...
            //       e.OrderBook is a clone/snapshot of the live order book.
            var top = e.OrderBook.Top;

            lock (_consoleSync)
            {
                Console.WriteLine($"  {top.Symbol}  -  Bid: {top.Bid.Price.ToString(".00000000")}  |  {top.MidMarketPrice().ToString(".00000000")}  |  Ask: {top.Ask.Price.ToString(".00000000")}  -  Spread: {top.Spread().ToString(".00000000")}");
            }
        }

        private static void OnKlineEvent(object sender, KlineEventArgs e)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($" Candlestick [{e.Candlestick.OpenTime}] - Is Final: {(e.IsFinal ? "YES" : "NO")}");
            }
        }

        private static void OnAccountUpdateEvent(object sender, AccountUpdateEventArgs e)
        {
            Display(e.Account);
        }

        private static void OnOrderUpdateEvent(object sender, OrderUpdateEventArgs e)
        {
            lock (_consoleSync)
            {
                Console.WriteLine();
                Console.WriteLine($"Order [{e.Order.Id}] update: {e.OrderExecutionType}");
                Display(e.Order);
                Console.WriteLine();
            }
        }

        private static void OnTradeUpdateEvent(object sender, TradeUpdateEventArgs e)
        {
            lock (_consoleSync)
            {
                Console.WriteLine();
                Console.WriteLine($"Order [{e.Order.Id}] update: {e.OrderExecutionType}");
                Display(e.Order);
                Console.WriteLine();
                Display(e.Trade);
                Console.WriteLine();
            }
        }

        private static void Display(AggregateTrade trade)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($"  {trade.Time().ToLocalTime()} - {trade.Symbol.PadLeft(8)} - {(trade.IsBuyerMaker ? "Sell" : "Buy").PadLeft(4)} - {trade.Quantity.ToString("0.00000000")} @ {trade.Price.ToString("0.00000000")}{(trade.IsBestPriceMatch ? "*" : " ")} - [ID: {trade.Id}] - {trade.Timestamp}");
            }
        }

        private static void Display(Candlestick candlestick)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($"  {candlestick.Symbol} - O: {candlestick.Open.ToString("0.00000000")} | H: {candlestick.High.ToString("0.00000000")} | L: {candlestick.Low.ToString("0.00000000")} | C: {candlestick.Close.ToString("0.00000000")} | V: {candlestick.Volume.ToString("0.00")} - [{candlestick.OpenTime}]");
            }
        }

        private static void Display(Order order)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($"  {order.Symbol.PadLeft(8)} - {order.Type.ToString().PadLeft(6)} - {order.Side.ToString().PadLeft(4)} - {order.OriginalQuantity.ToString("0.00000000")} @ {order.Price.ToString("0.00000000")} - {order.Status.ToString()}  [ID: {order.Id}]");
            }
        }

        private static void Display(AccountTrade trade)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($"  {trade.Time().ToLocalTime().ToString().PadLeft(22)} - {trade.Symbol.PadLeft(8)} - {(trade.IsBuyer ? "Buy" : "Sell").PadLeft(4)} - {(trade.IsMaker ? "Maker" : "Taker")} - {trade.Quantity.ToString("0.00000000")} @ {trade.Price.ToString("0.00000000")}{(trade.IsBestPriceMatch ? "*" : " ")} - Fee: {trade.Commission.ToString("0.00000000")} {trade.CommissionAsset.PadLeft(5)} [ID: {trade.Id}]");
            }
        }

        private static void Display(AccountInfo account)
        {
            lock (_consoleSync)
            {
                Console.WriteLine($"    Maker Commission:  {account.Commissions.Maker.ToString().PadLeft(3)} %");
                Console.WriteLine($"    Taker Commission:  {account.Commissions.Taker.ToString().PadLeft(3)} %");
                Console.WriteLine($"    Buyer Commission:  {account.Commissions.Buyer.ToString().PadLeft(3)} %");
                Console.WriteLine($"    Seller Commission: {account.Commissions.Seller.ToString().PadLeft(3)} %");
                Console.WriteLine($"    Can Trade:    {(account.Status.CanTrade ? "Yes" : "No").PadLeft(3)}");
                Console.WriteLine($"    Can Withdraw: {(account.Status.CanWithdraw ? "Yes" : "No").PadLeft(3)}");
                Console.WriteLine($"    Can Deposit:  {(account.Status.CanDeposit ? "Yes" : "No").PadLeft(3)}");
                Console.WriteLine();
                Console.WriteLine($"    Balances (only amounts > 0):");

                Console.WriteLine();
                foreach (var balance in account.Balances)
                {
                    if (balance.Free > 0 || balance.Locked > 0)
                    {
                        Console.WriteLine($"      Asset: {balance.Asset} - Free: {balance.Free} - Locked: {balance.Locked}");
                    }
                }
                Console.WriteLine();
            }
        }
    }
}