// This program loads the SQL Server CBOEOptions Database with data from CBOEDatashop
// It also computes Greeks for those options (it throws away the greeks from CBOEDataShop
// It uses my modified version of Jaeckel's Lets Be Rational C++ program to compute option greeks

#define ONLY25STRIKES

using System.IO.Compression;
using LetsBeRationalLib;
using System.Diagnostics;
using ReadFredTreasuryRates;
//using MySql.Data.MySqlClient;

namespace LoadOptionDataFromCBOEData
{
    using ExpirationDate = DateOnly;
    using Strike = Int16;
    using System.Text;
    using System.Collections.Concurrent;

    class OptionData
    {
        internal DateTime QuoteDateTime;
        internal DateOnly Expiration;
        internal int Strike;
        internal LetsBeRational.OptionType OptionType;
        internal string Root = "SPX";
        internal float Underlying;
        internal float Bid;
        internal float Ask;
        internal float ImpliedVolatility;
        internal float Delta;
        internal float Gamma;
        internal float Theta;
        internal float Vega;
        internal float Rho;
        internal float OpenInterest;
        internal float RiskFreeRate;
        internal float DividendYield;
    }

    // for reading CBOE Data
    public enum CBOEFields : int
    {
        UnderlyingSymbol,
        QuoteDateTime,
        Root,
        Expiration,
        Strike,
        OptionType,
        Open,
        High,
        Low,
        Close,
        TradeVolume,
        BidSize,
        Bid,
        AskSize,
        Ask,
        UnderlyingBid,
        UnderlyingAsk,
        ImpliedUnderlyingPrice,
        ActiveUnderlyingPrice,
        ImpliedVolatility,
        Delta,
        Gamma,
        Theta,
        Vega,
        Rho,
        OpenInterest
    }

    class Program
    {
        const int minStrike = 625;
        const int maxStrike = 10000;
        const int maxDTE = 200; // for saving data

        ConcurrentDictionary<(LetsBeRational.OptionType, string root, ExpirationDate, int strike, DateOnly), SortedList<TimeOnly, OptionData>> mainDict = new();

        const string DataDir = @"C:\Users\lel48\CBOEDataShop\SPX";
        const string expectedHeader = "underlying_symbol,quote_datetime,root,expiration,strike,option_type,open,high,low,close,trade_volume,bid_size,bid,ask_size,ask,underlying_bid,underlying_ask,implied_underlying_price,active_underlying_price,implied_volatility,delta,gamma,theta,vega,rho,open_interest";
        readonly StreamWriter errorLog = new(Path.Combine(DataDir, "error_log.txt"));

        static readonly DateTime earliestDate = new(2013, 1, 1);
        readonly FredRateReader rate_reader;
        readonly SP500DividendYieldReader dividend_reader;

        static readonly Stopwatch watch = new();

        static void Main()
        {
            watch.Start();

            try
            {
                var program = new Program();
                program.Run();
            }
            catch (Exception ex)
            {
                //display error message
                Console.WriteLine("Exception: " + ex.Message);
                return;
            }

            watch.Stop();
            Console.WriteLine($"LoadOptionDataFromCBOEData Elpased time: {watch.ElapsedMilliseconds / 1000.0}");
        }

        Program()
        {
            rate_reader = new FredRateReader(earliestDate);
            dividend_reader = new SP500DividendYieldReader(earliestDate);
        }

        bool LogError(string error)
        {
            errorLog.WriteLine(error);
            return false;
        }

        void Run()
        {
            // CBOEDataShop 15 minute data (900sec); a separate zip file for each day, so, if programmed correctly, we can read each day in parallel
            string[] zipFileNameArray = Directory.GetFiles(DataDir, "UnderlyingOptionsIntervals_900sec_calcs_oi*.zip", SearchOption.AllDirectories); // filename if you bought greeks
            //string[] zipFileNameArray = Directory.GetFiles(DataDir, "UnderlyingOptionsIntervalsQuotes_900sec*.zip", SearchOption.AllDirectories); // filename if you didn't buy greeks
            Array.Sort(zipFileNameArray);

            // now read actual option data from each zip file (we have 1 zip file per day), row by row, and add it to SortedList for that date
            int numFilesRead = 0;
            Parallel.ForEach(zipFileNameArray, new ParallelOptions { MaxDegreeOfParallelism = 16 }, (zipFileName) =>
            {
                using ZipArchive archive = ZipFile.OpenRead(zipFileName);
                Interlocked.Increment(ref numFilesRead);
                Console.WriteLine($"Processing file {numFilesRead}: {zipFileName}");
                string fileName = archive.Entries[0].Name;
                if (archive.Entries.Count != 1)
                    Console.WriteLine($"Warning: {zipFileName} contains more than one file ({archive.Entries.Count}). Processing first one: {fileName}");
                ZipArchiveEntry zip = archive.Entries[0];
                DateTime zipDate = DateTime.Parse(zipFileName.Substring(zipFileName.Length - 14, 10));

                // the main data structure we are filling in
                Dictionary<ExpirationDate, List<OptionData>> expirationDictionary = new();

                using (StreamReader reader = new(zip.Open()))
                {
                    bool validOption;
                    OptionData option;

                    // read header
                    string line;
                    try
                    {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                        line = reader.ReadLine();
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                        if (line == null)
                            return;
                    }
                    catch (System.IO.InvalidDataException ex)
                    {
                        string errmsg = $"*Error* InvalidDataException reading file {zipFileName} Row 1 Message {ex.Message}";
                        Console.WriteLine(errmsg);
                        LogError(errmsg);
                        return;
                    }

                    if (!line.StartsWith(expectedHeader))
                    {
                        Console.WriteLine($"Warning: file {fileName} does not have expected header: {line}. Line skiped anyways");
                        Console.WriteLine($"         Expected header: {expectedHeader}");
                    }

                    int rowIndex = 1; // header was row 0, but will be row 1 if we look at data in Excel
                    int numValidOptions = 0;
                    while (true)
                    {
                        try
                        {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                            line = reader.ReadLine();
#pragma warning restore CS8600 // Converting null literal or possible null value to non-nullable type.
                            if (line == null)
                                break;
                        }
                        catch (System.IO.InvalidDataException ex)
                        {
                            string errmsg = $"*Error* InvalidDataException reading file {zipFileName} Row {rowIndex + 1} Message {ex.Message}";
                            Console.WriteLine(errmsg);
                            LogError(errmsg);
                            break;
                        }
                        ++rowIndex;
                        option = new OptionData();
                        validOption = ParseOption(maxDTE, line, option, zipDate, fileName, rowIndex);
                        if (validOption)
                        {
                            numValidOptions++;

                            // before creating collections for indexing, we have to make sure:
                            // 1. if there are SPX and SPXW/SPXQ options for the same Expiration, we throw away the SPXW or SPXQ. If there are SPXW
                            //    and SPXQ options for the same Expiration, we throw away the SPXQ 
                            // 2. If there are options with the same Expiration but different strikes, but with the same Delta, we adjust Delta so that
                            //    if a call, the Delta of the higher Strike is strictly less than the Delta of of a lower Strike, and 
                            //    if a put, the Delta of the higher Strike is strictly greater than the Delta of a lower Strike.
                            //    We do this by minor adjustments to "true" Delta
                            bool expirationFound = expirationDictionary.TryGetValue(option.Expiration, out List<OptionData>? optionList);
                            if (!expirationFound)
                            {
                                optionList = new List<OptionData> { option };
                                expirationDictionary.Add(option.Expiration, optionList);
                            }
                            else
                            {
                                Debug.Assert(optionList != null);
                                Debug.Assert(optionList.Count > 0);
                                OptionData optionInList = optionList.First();
                                if (option.Root == optionInList.Root)
                                    optionList.Add(option);
                                else
                                {
                                    if (optionInList.Root == "SPX")
                                        continue; // throw away new SPXW/SPXQ option that has same Expiration as existing SPX option

                                    if (option.Root == "SPX" || option.Root == "SPXW")
                                    {
                                        // throw away existing List and replace it with new list of options of root of new option
                                        optionList.Clear();
                                        optionList.Add(option);
                                    }
                                }
                            }
                        }
                    }
                }

                // now write each item in expirationDictionary into mainDict
                foreach (List<OptionData> optionList in expirationDictionary.Values)
                {
                    foreach (OptionData optionData in optionList)
                        InsertOptionDataIntoDatabase(optionData);
                }
            });

            // now print out how many days of data we have for each expiration and strike
        }

        void InsertOptionDataIntoDatabase(OptionData optionData)
        {
            var key = (optionData.OptionType, optionData.Root, optionData.Expiration, optionData.Strike, DateOnly.FromDateTime(optionData.QuoteDateTime));
            var list = mainDict.GetOrAdd(key, (key) => new SortedList<TimeOnly, OptionData>());
            list.Add(TimeOnly.FromDateTime(optionData.QuoteDateTime), optionData);
        }

        bool ParseOption(int maxDTE, string line, OptionData option, DateTime zipDate, string fileName, int linenum)
        {
            Debug.Assert(option != null);

            string[] fields = line.Split(',');

            if (fields[0] != "^SPX")
                return LogError($"*Error*: underlying_symbol is not ^SPX for file {fileName}, line {linenum}, underlying_symbol {fields[0]}, {line}");

            option.Root = fields[2].Trim().ToUpper();
            if (option.Root != "SPX" && option.Root != "SPXW" && option.Root != "SPXQ")
            {
                if (option.Root == "BSZ" || option.Root == "SRO")
                    return false; // ignore binary options on SPX
                return LogError($"*Error*: root is not SPX, SPXW, or SPXQ for file {fileName}, line {linenum}, root {option.Root}, {line}");
            }

            string optionType = fields[5].Trim().ToUpper();
            if (optionType != "P" && optionType != "C")
                return LogError($"*Error*: option_type is neither 'P' or 'C' for file {fileName}, line {linenum}, root {option.Root}, {line}");
            option.OptionType = (optionType == "P") ? LetsBeRational.OptionType.Put : LetsBeRational.OptionType.Call;

            //row.dt = DateTime.ParseExact(fields[1], "yyyy-MM-dd HH:mm:ss", provider);
            option.QuoteDateTime = DateTime.Parse(fields[(int)CBOEFields.QuoteDateTime]);
            Debug.Assert(option.QuoteDateTime.Date == zipDate); // you can have many, many options at same date/time (different strikes)

            // not ever interested in options after 16:00:00
            switch (option.QuoteDateTime.Hour)
            {
                case 16:
                    if (option.QuoteDateTime.Minute > 0)
                        return false;
                    break;
            }

#if NO_CALLS
            // we're not interested in Calls right now
            if (option.OptionType == LetsBeRational.OptionType.Call)
                return false;
#endif
            option.Strike = (int)(float.Parse(fields[(int)CBOEFields.Strike]) + 0.001f); // +.001 to prevent conversion error
                                                                                         // for now, only conside strikes with even multiples of 25
#if ONLY25STRIKES
            if (option.Strike % 25 != 0)
                return false;
#endif
            if (option.Strike < minStrike || option.Strike > maxStrike)
                return false;

            option.Underlying = float.Parse(fields[(int)CBOEFields.UnderlyingBid]);
            if (option.Underlying <= 0.0)
                return LogError($"*Error*: underlying_bid is 0 for file {fileName}, line {linenum}, {line}");
            if (option.Underlying < 500.0)
                return LogError($"*Error*: underlying_bid is less than 500 for file {fileName}, line {linenum}, {line}");

            //row.Expiration = DateTime.ParseExact(fields[3], "yyyy-mm-dd", provider);
            option.Expiration = DateOnly.Parse(fields[(int)CBOEFields.Expiration]);

            int dte = option.Expiration.DayNumber - DateOnly.FromDateTime(option.QuoteDateTime).DayNumber;
            if (dte < 0)
                return LogError($"*Error*: quote_datetime is later than expiration for file {fileName}, line {linenum}, {line}");

            // we're not interested in dte greater than 180 days
            if (dte > maxDTE)
                return false;

            option.Bid = float.Parse(fields[(int)CBOEFields.Bid]);
            if (option.Bid < 0f)
                return LogError($"*Error*: bid is less than 0 for file {fileName}, line {linenum}, bid {option.Bid}, {line}");
            option.Ask = float.Parse(fields[(int)CBOEFields.Ask]);
            if (option.Ask < 0f)
                return LogError($"*Error*: ask is less than 0 for file {fileName}, line {linenum}, ask {option.Ask}, {line}"); ;
            option.OpenInterest = int.Parse(fields[(int)CBOEFields.OpenInterest]);

#if true
            LetsBeRational.OptionType lbtype = LetsBeRational.OptionType.Put;
            double price = (87.5 + 90.3) / 2; ;
            double rr = 0.00163; // risk free rate(1 year treasury yield)
            double d = 0.0194; // trailing 12 - month sp500 dividend yield
            DateTime quote_dt = new(2014, 1, 2);
            DateTime exp_dt = new(2014, 1, 31);
            int this_dte = (exp_dt - quote_dt).Days;
            double tt = this_dte / 365.0; // days to expiration / days in year
            double ss = 1837.73; // underlying SPX price
            double KK = 1925.0;
            double intrinsic = (lbtype == LetsBeRational.OptionType.Put) ? ((ss < KK) ? KK - ss : 0) : ((ss > KK) ? ss - KK : 0);
            double iv = LetsBeRational.ImpliedVolatility(price, ss, KK, tt, rr, d, lbtype);
            double del = LetsBeRational.Delta(ss, KK, tt, rr, iv, d, option.OptionType);
            double th = LetsBeRational.Theta(ss, KK, tt, rr, iv, d, option.OptionType);
            double gam = LetsBeRational.Gamma(ss, KK, tt, rr, iv, d, option.OptionType);
            double veg = LetsBeRational.Vega(ss, KK, tt, rr, iv, d, option.OptionType);
            double rh = LetsBeRational.Rho(ss, KK, tt, rr, iv, d, option.OptionType);
#endif

            double mid = (0.5 * (option.Bid + option.Ask));
            if (mid == 0)
            {
                option.ImpliedVolatility = option.Delta = option.Gamma = option.Vega = option.Rho = 0f;
                return true; // I keep this option in case it is in a Position
            }

            double dteFraction = dte;
            if (dte == 0)
                dteFraction = (option.QuoteDateTime.TimeOfDay.TotalSeconds - 9 * 3600 + 1800) / (390 * 60); // fraction of 390 minute main session
            double t = dteFraction / 365.0; // days to Expiration / days in year
            double s = option.Underlying; // underlying SPX price
            double K = (double)option.Strike; // Strike price
            double q = option.DividendYield = 0.01f * dividend_reader.DividendYield(option.Expiration.ToDateTime(new TimeOnly())); // 1.29% Oct-31-2021
            double r = option.RiskFreeRate = 0.01f * rate_reader.RiskFreeRate(option.QuoteDateTime, dte); // 0.05% SOFR on 11/19/2021
            option.ImpliedVolatility = (float)LetsBeRational.ImpliedVolatility(mid, s, K, t, r, q, option.OptionType);
            if (float.IsInfinity(option.ImpliedVolatility))
            {
                option.ImpliedVolatility = float.IsPositiveInfinity(option.ImpliedVolatility) ? 1f : -1f;
                option.Delta = option.Theta = option.Gamma = option.Vega = option.Rho = 0f;
                return true;
            }

            if (float.IsNaN(option.ImpliedVolatility))
            {
                int ttt = 1;
            }

            option.Delta = (float)LetsBeRational.Delta(s, K, t, r, option.ImpliedVolatility, q, option.OptionType);
            option.Theta = (float)LetsBeRational.Theta(s, K, t, r, option.ImpliedVolatility, q, option.OptionType);
            option.Gamma = (float)LetsBeRational.Gamma(s, K, t, r, option.ImpliedVolatility, q, option.OptionType);
            option.Vega = (float)LetsBeRational.Vega(s, K, t, r, option.ImpliedVolatility, q, option.OptionType);
            option.Rho = (float)LetsBeRational.Rho(s, K, t, r, option.ImpliedVolatility, q, option.OptionType);
            if (option.Delta == 0f)
            {
                int aaa = 1;
            }
            return true;
        }
    }
}
