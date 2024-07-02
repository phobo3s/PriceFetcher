using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata;
using System.Threading.Tasks;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal class Program
{
    // options dictionary details in the GetConfig method.
    static Dictionary<string,string> options = new Dictionary<string, string>();
    // Main prices.csv path. it is like a database for prices.
    static string filePath = "Commodity-Prices.csv";
    // Define date formats to handle different formats
    static string[] dateFormats = { "dd/MM/yyyy", "dd.MM.yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d.MM.yyyy", "yyyy-MM-dd" };



    static void Main(string[] args)
    {
        Console.WriteLine("Hello and welcome to price Fetcher.");
        GetConfig();
        bool exit = false;
        while(!exit) 
        {
            exit = WriteMenuGetSelection();
        }

    }
    static bool WriteMenuGetSelection()
    {
        Console.WriteLine("Please select an option. press Enter for exit.");
        Console.WriteLine("1. Price Csv Update/Create");
        Console.WriteLine("2. Parse Prices");
        Console.WriteLine("3. Get Config");
        string? userInput = Console.ReadLine();
        bool exit = false;
        switch (userInput)
        {
            case "":
                exit = true;
                break;
            case "1":
                Main2().Wait(); // 
                break;
            case "2":
                ParsePrices();
                break;
            case "3":
                GetConfig();
                break;
            default:
                Console.WriteLine("This is out of my imagination. please select again.");
                break;
        }
        return exit;
    }

    

    static void GetConfig()
    {
        //valid config examples:
        //------------------------
        //StockPostfix:.IS
        //CommodityCurrency:TRY
        //------------------------
        string configFilePath = "PriceFetcher.config";
        if (File.Exists(configFilePath))
        {
            options.Clear();
            using (var reader = new StreamReader(configFilePath))
            {
                string line = "";

                while (!reader.EndOfStream)
                {
                    line = reader.ReadLine();
                    try
                    {
                        options.Add(line.Split(":")[0], line.Split(":")[1]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("\nException Caught!");
                        Console.WriteLine("Message :{0} ", ex.Message);
                    }
                }
                Console.WriteLine("Config loaded.");
            }
        }
        else
        {
            Console.WriteLine("Config file not found. Created one only for you.");
            File.Create(configFilePath).Dispose();
        }
    }

    static async Task Main2()
    {
        HttpClient client = new HttpClient();
        var data = new Dictionary<string, Dictionary<long, double?>>();
        
        string commoditiesStartCsvPath = "Commodity-StartEnds.csv";
        DateTime latestDate = DateTime.MinValue;
        bool updateOnlyNewData = false;
        bool appendToFile = false;
        List<string> symbols = new List<string>();
        var symbolsWithDates = new Dictionary<string, DateTime>();

        // Check if the file exists and read the latest date

        if (File.Exists(filePath))
        {
            using (var reader = new StreamReader(filePath))
            {
                // Read header to extract symbols
                try
                { 
                    var headerLine = reader.ReadLine();
                    if (headerLine == null) { throw new NullReferenceException(); };
                    var headers = headerLine.Split(';');
                    for (int i = 1; i < headers.Length; i++)
                    {
                        symbols.Add(headers[i]);
                    }
                    while (!reader.EndOfStream)
                    {
                        var line = reader.ReadLine();
                        var values = line.Split(';');
                        DateTime date = DateTime.ParseExact(values[0], dateFormats, CultureInfo.InvariantCulture);
                        if (date > latestDate)
                        {
                            latestDate = date;
                        }
                    }
                }
                catch(NullReferenceException ex)
                {
                    Console.WriteLine("CSV not formatted properly. Exiting");
                    return;
                }
            }
            // Ask the user if they want to update only new data
            Console.WriteLine("Do you want to update only new data? (Yes/No): ");
            string? userInput1 = Console.ReadLine();
            if (userInput1.Trim().ToLower() == "yes" | userInput1.Trim().ToLower() == "y")
            {
                updateOnlyNewData = true;
                appendToFile = true;
            }
        }
        else
        {
            Console.WriteLine($"commodities prices file {filePath} not found.");
            //return;
            File.Create(filePath).Dispose();
        }
        //check and read the commodities csv for start dates
        if (File.Exists(commoditiesStartCsvPath))
        {
            using (var reader = new StreamReader(commoditiesStartCsvPath)) 
            { 
                symbols.Clear();
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(';');
                    symbols.Add(values[0]);
                    symbolsWithDates.Add(values[0], DateTime.ParseExact(values[1], dateFormats, CultureInfo.InvariantCulture));
                }
            }
        }
        else
        {
            Console.WriteLine($"commodities start dates file {commoditiesStartCsvPath} not found.");
        }
        string stockPrefix = ""; //default stock prefix ??
        if (options.ContainsKey("StockPostfix")) { stockPrefix = options["StockPostfix"]; }
        try
        {
            string customUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/111.0.0.0 Safari/537.36";
            client.DefaultRequestHeaders.Add("User-Agent", customUserAgent);
            long period1 = 0;
            foreach (var symbol in symbols)
            {
                period1 = 0; // first transaction unix date
                if (symbolsWithDates.ContainsKey(symbol))
                {
                    period1 = new DateTimeOffset(symbolsWithDates[symbol]).ToUnixTimeSeconds();
                }
                if (updateOnlyNewData && latestDate != DateTime.MinValue)
                {
                    period1 = new DateTimeOffset(latestDate.AddDays(1)).ToUnixTimeSeconds();
                }
                if (symbol.Length == 3 && symbol != "USD" && symbol != "EUR" && symbol != "GBP")
                {
                    // Send a GET request to the Yahoo Finance API for other symbols
                        string url = $"https://api.fintables.com/funds/{symbol}/chart/?start_date={DateTimeOffset.FromUnixTimeSeconds(period1).UtcDateTime.ToString("yyyy-MM-dd")}&compare=";
                        HttpResponseMessage response = await client.GetAsync(url);
                        string responseBody = "";
                        JObject myObject = new JObject();
                        JArray timestampCloses = new JArray();
                    try
                    {
                        response.EnsureSuccessStatusCode();
                        responseBody = await response.Content.ReadAsStringAsync();
                        myObject = (JObject)JsonConvert.DeserializeObject(responseBody);
                        timestampCloses = (JArray)myObject["results"]["data"];
                    }
                    catch (HttpRequestException e)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Console.WriteLine($"not found one. {symbol}");
                        }
                    }
                    finally 
                    {
                        data[symbol] = new Dictionary<long, double?>();
                        for (int i = 0; i < timestampCloses.Count(); i++)
                        {
                            double? closeValue = timestampCloses[i][symbol].Type == JTokenType.Null ? (double?)null : (double)timestampCloses[i][symbol];
                            data[symbol][(long)((DateTimeOffset)DateTime.ParseExact((string)timestampCloses[i]["date"], dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)).ToUnixTimeSeconds()] = closeValue;
                        }
                    }
                }
                else
                {
                    // Send a GET request to the Yahoo Finance API for other symbols
                    string url = $"https://query1.finance.yahoo.com/v7/finance/chart/{symbol}{stockPrefix}?period1={period1}&period2={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&interval=1d&events=history&includeAdjustedClose=true";
                    HttpResponseMessage response = await client.GetAsync(url);
                    string responseBody = "";
                    JObject myObject = new JObject();
                    JArray timestamps = new JArray();
                    JArray closes = new JArray();
                    try
                    {
                        response.EnsureSuccessStatusCode();
                        responseBody = await response.Content.ReadAsStringAsync();
                        myObject = (JObject)JsonConvert.DeserializeObject(responseBody);
                        timestamps = (JArray)myObject["chart"]["result"][0]["timestamp"];
                        closes = (JArray)myObject["chart"]["result"][0]["indicators"]["quote"][0]["close"];
                    }
                    catch (HttpRequestException e)
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound)
                        {
                            Console.WriteLine($"not found one. {symbol}");
                        }
                    }
                    finally 
                    {
                        data[symbol] = new Dictionary<long, double?>();
                        for (int i = 0; i < timestamps.Count(); i++)
                        {
                            double? closeValue = closes[i].Type == JTokenType.Null ? (double?)null : (double)closes[i];
                            data[symbol][(long)Math.Floor((decimal)timestamps[i] / 86400)*86400 ] = closeValue;
                        }
                    }
                }
                Console.WriteLine($"{symbol} downladed");
            }
            var allTimestamps = new SortedSet<long>();
            foreach (var symbolData in data.Values)
            {
                foreach (var timestamp in symbolData.Keys)
                {
                    allTimestamps.Add(timestamp);
                }
            }
            using (var writer = new StreamWriter(filePath, append: appendToFile))
            {
                if (!appendToFile)
                {
                    // Write header if the file didn't exist or if overwriting
                    writer.Write("Date");
                    foreach (var symbol in symbols)
                    {
                        writer.Write($";{symbol}");
                    }
                    writer.WriteLine();
                }
                foreach (var timestamp in allTimestamps)
                {
                    var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
                    DateTime parsedDate = DateTime.ParseExact(date, dateFormats, CultureInfo.InvariantCulture);
                    if (!updateOnlyNewData || parsedDate > latestDate)
                    {
                        writer.Write($"{date}");
                        foreach (var symbol in symbols)
                        {
                            if (data[symbol].TryGetValue(timestamp, out var close))
                            {
                                writer.Write($";{close?.ToString() ?? ""}");
                            }
                            else
                            {
                                writer.Write(";");
                            }
                        }
                        writer.WriteLine();
                    }
                }
            }
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine("\nException Caught!");
            Console.WriteLine("Message :{0} ", e.Message);
        }
        catch (Exception e)
        {
            Console.WriteLine("\nException Caught!");
            Console.WriteLine("Message :{0} ", e.Message);
        }
    }
    private static void ParsePrices()
    {
        string postFixForCommodities = "TRY"; //default commodity currency
        if (options.ContainsKey("CommodityCurrency")) { postFixForCommodities = options["CommodityCurrency"]; };
        List<string> symbols = new List<string>();
        File.Create("Commodity-Prices.txt").Dispose();
        using (var writer = new StreamWriter("Commodity-Prices.txt", append: false))
        {
            using (var reader = new StreamReader(filePath))
            {
                // Read header to extract symbols
                var headerLine = reader.ReadLine();
                var headers = headerLine.Split(';');
                writer.WriteLine("; Commodities");
                for (int i = 1; i < headers.Length; i++)
                {
                    symbols.Add(headers[i]);
                    writer.WriteLine($"commodity {headers[i]} 1.000,00");
                }
                writer.WriteLine("; ---");
                writer.WriteLine("; Prices");
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(';');
                    DateTime date = DateTime.ParseExact(values[0], dateFormats, CultureInfo.InvariantCulture);
                    for (int i = 1; i < values.Length; i++)
                    {
                        if (values[i] != "")
                        {
                            writer.WriteLine($"P    {date.ToString("yyyy-MM-dd")}    {headers[i]}    {values[i]} {postFixForCommodities}");
                        }
                    }

                }
            }
        }
        Console.WriteLine("creating txt file for commodities done!");
    }
}