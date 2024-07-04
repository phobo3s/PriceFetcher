using System;
using System.Diagnostics;
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
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using static System.Net.WebRequestMethods;
using System.Xml.Linq;
using System.Net.Http.Headers;

internal class Program
{
    // options dictionary details in the GetConfig method.
    static Dictionary<string,string> options = new Dictionary<string, string>();
    // Main prices.csv path. it is like a database for prices.
    static string filePath = "Commodity-Prices.csv";
    // Define date formats to handle different formats
    static string[] dateFormats = { "dd/MM/yyyy", "dd.MM.yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d.MM.yyyy", "yyyy-MM-dd" };
    // write some comment here.
    static string commoditiesStartCsvPath = "Commodity-StartEnds.csv";

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
        Console.WriteLine("3. Create commodity start/end dates Csv");
        Console.WriteLine("4. Get Config");
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
                CreateCommStartEndFile();
                break;
            case "4":
                GetConfig();
                break;
            default:
                Console.WriteLine("This is out of my imagination. please select again.");
                break;
        }
        return exit;
    }

    private static void CreateCommStartEndFile()
    {
        string startEndFilePath = "Commodity-StartEnds.csv";
        if (!System.IO.File.Exists(startEndFilePath))
        { 
            Console.WriteLine("Commodity start end file not found. Creating one only for you. This may take a while.");
            System.IO.File.Create(startEndFilePath).Dispose();
        }
        else
        {
            Console.WriteLine("This may take a while.");
        }
        // hledger commodities komutunu çalıştır ve çıktısını al
        var commodities = ExecuteShellCommand("hledger commodities")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        using (var writer = new StreamWriter(startEndFilePath, append:false))
        {
            int totalCommodities = commodities.Length;
            int currentCommodityIndex = 0;
            foreach (var commodity in commodities)
            {
                // İlerleme çubuğunu güncelle
                UpdateProgressBar(currentCommodityIndex + 1, totalCommodities);
                // hledger reg cur:<commodity> komutunu çalıştır ve çıktısını al
                var register = ExecuteShellCommand($"hledger reg cur:{commodity} -R -O csv");
                string[] lines = register.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                string firstDate = Regex.Split(lines[1], "\",\"")[1].Trim('\"').Trim();
                string lastDate = Regex.Split(lines.Last(), "\",\"").Last().Trim('\"').Trim() == "0" | lines.Count() == 1 ? Regex.Split(lines.Last(), "\",\"")[1].Trim('\"').Trim() : "---";
                writer.WriteLine($"{commodity};{firstDate};{lastDate}");
                currentCommodityIndex++;
            }
        }
    }
    static void UpdateProgressBar(int current, int total)
    {
        Console.CursorVisible = false;
        Console.SetCursorPosition(0, Console.CursorTop);
        int barLength = 30;
        decimal progress = (decimal)current / total;
        int progressChars = (int)(barLength * progress);
        Console.Write($"[{new string('#', progressChars)}{new string('-', barLength - progressChars)}] {current}/{total}   ");
    }
    static void GetConfig()
    {
        //valid config examples:
        //------------------------
        //StockPostfix:.IS
        //CommodityCurrency:TRY
        //------------------------
        string configFilePath = "PriceFetcher.config";
        if (System.IO.File.Exists(configFilePath))
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
            System.IO.File.Create(configFilePath).Dispose();
        }
    }
    static async Task Main2()
    {
        HttpClient client = new HttpClient();
        var data = new Dictionary<string, Dictionary<long, double?>>();
        DateTime latestDate = DateTime.MinValue;
        bool updateOnlyNewData = false;
        bool appendToFile = false;
        List<string> symbols = new List<string>();
        var symbolsWithDates = new Dictionary<string, string[]>();

        // Check if the file exists and read the latest date

        if (System.IO.File.Exists(filePath))
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
            System.IO.File.Create(filePath).Dispose();
        }
        //check and read the commodities csv for start dates
        if (System.IO.File.Exists(commoditiesStartCsvPath))
        {
            using (var reader = new StreamReader(commoditiesStartCsvPath)) 
            { 
                symbols.Clear();
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    var values = line.Split(';');
                    symbols.Add(values[0]);
                    string[] vals = {values[1], values[2]};
                    symbolsWithDates.Add(values[0], vals);
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
            
            long period1 = 0;
            long period2 = 0;
            foreach (var symbol in symbols)
            {
                client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/111.0.0.0 Safari/537.36");

                period1 = 0; // first transaction unix date
                period2 = (long)new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(); //today unix date
                if (symbolsWithDates.ContainsKey(symbol))
                {
                    period1 = ((DateTimeOffset)(DateTime.ParseExact(symbolsWithDates[symbol][0], dateFormats, CultureInfo.InvariantCulture))).ToUnixTimeSeconds();
                    if (symbolsWithDates[symbol][1] == "---")
                    {
                        //
                    }
                    else
                    {
                        period2 = ((DateTimeOffset)(DateTime.ParseExact(symbolsWithDates[symbol][1], dateFormats, CultureInfo.InvariantCulture).AddDays(1))).ToUnixTimeSeconds();
                    }
                }
                if (updateOnlyNewData && latestDate != DateTime.MinValue)
                {
                    period1 = new DateTimeOffset(latestDate.AddDays(1)).ToUnixTimeSeconds();
                }
                // data request and writing to dictionry starting here.
                if (symbol.Length == 3 && symbol != "USD" && symbol != "EUR" && symbol != "GBP")
                {
                    var handler = new HttpClientHandler() { UseCookies = false, AllowAutoRedirect = true };
                    client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
                    client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
                    client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
                    client.DefaultRequestHeaders.Add("Origin", "https://www.tefas.gov.tr");
                    client.DefaultRequestHeaders.Referrer = new Uri("https://www.tefas.gov.tr");
                    string url = "https://www.tefas.gov.tr/api/DB/BindHistoryInfo";
                    //
                    DateTimeOffset startDate = DateTimeOffset.FromUnixTimeSeconds(period1).UtcDateTime;
                    DateTimeOffset endDate = DateTimeOffset.FromUnixTimeSeconds(period2).UtcDateTime;
                    //
                    DateTimeOffset currentStartDate = startDate;
                    data[symbol] = new Dictionary<long, double?>();
                    while (currentStartDate < endDate)
                    {
                        //
                        DateTimeOffset currentEndDate = currentStartDate.AddMonths(3);
                        if (currentEndDate >= endDate)
                        {
                            currentEndDate = endDate;
                        }else if (currentEndDate < startDate)
                        {
                            //currentEndDate == currentEndDate;
                        }
                        string package = $"fontip=YAT&sfontur=&fonkod={symbol}&fongrup=&bastarih={currentStartDate.ToString("dd.MM.yyyy")}&bittarih={currentEndDate.ToString("dd.MM.yyyy")}&fonturkod=&fonunvantip=";
                        HttpContent _Body = new StringContent(package, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");                   
                        string responseBody = "";
                        JObject myObject = new JObject();
                        JArray timestampCloses = new JArray();
                        var response = new HttpResponseMessage { };
                        try
                        {
                            response = client.PostAsync(url, _Body).Result;
                            response.EnsureSuccessStatusCode();
                            responseBody = response.Content.ReadAsStringAsync().Result;
                            myObject = (JObject)JsonConvert.DeserializeObject(responseBody);
                            timestampCloses = (JArray)myObject["data"];
                        }
                        catch (HttpRequestException e)
                        {
                            if (response.StatusCode == HttpStatusCode.NotFound)
                            {
                                Console.WriteLine($"not found one. {symbol}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("\nException Caught!");
                            Console.WriteLine("Message :{0} ", ex.Message);
                        }
                        finally
                        {
                            
                            for (int i = 0; i < timestampCloses.Count(); i++)
                            {
                                if ((long)Math.Floor((decimal)timestampCloses[i]["TARIH"] / 86400000) * 86400 > period2)
                                {
                                    i = timestampCloses.Count();
                                }
                                else
                                {
                                    double? closeValue = timestampCloses[i]["FIYAT"].Type == JTokenType.Null ? (double?)null : (double)timestampCloses[i]["FIYAT"];
                                    data[symbol][(long)Math.Floor((decimal)timestampCloses[i]["TARIH"] / 86400000) * 86400] = closeValue;
                                }
                            }
                        }
                        currentStartDate = currentStartDate.AddMonths(3);
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
                            if (Math.Floor((decimal)timestamps[i] / 86400) * 86400 > period2)
                            {
                                i = timestamps.Count();
                            }
                            else
                            {
                                double? closeValue = closes[i].Type == JTokenType.Null ? (double?)null : (double)closes[i];
                                data[symbol][(long)Math.Floor((decimal)timestamps[i] / 86400) * 86400] = closeValue;
                            }
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
        System.IO.File.Create("Commodity-Prices.txt").Dispose();
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
                    writer.WriteLine($"commodity {(headers[i].Any(char.IsDigit) ? "\"" + headers[i] + "\""  : headers[i])} 1.000,00");
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
                            writer.WriteLine($"P    {date.ToString("yyyy-MM-dd")}    {(headers[i].Any(char.IsDigit) ? "\"" + headers[i] + "\"" : headers[i])}    {values[i]} {postFixForCommodities}");
                        }
                    }

                }
            }
        }
        Console.WriteLine("creating txt file for commodities done!");
    }
    static string ExecuteShellCommand(string command)
    {
        string shell = "/bin/bash";
        string args = $"-c \"{command}\"";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            shell = "cmd.exe";
            args = $"/c \"{command}\"";
        }
        var processInfo = new ProcessStartInfo(shell, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(processInfo);
        using var reader = process.StandardOutput;
        string result = reader.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new Exception($"Komut çalıştırılamadı: {command}");
        }
        return result;
    }
}


// Send a GET request to the Yahoo Finance API for other symbols
//client.DefaultRequestHeaders.Add("authority", "api.fintables.com");
//client.DefaultRequestHeaders.Add("path", "/funds/AES/chart/?start_date=2021-10-03&compare=");
//client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");
//client.DefaultRequestHeaders.Add("Cache-Control", "max-age=0");
//client.DefaultRequestHeaders.Add("Sec-Ch-Ua-Platform", "Windows");
//client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
//client.DefaultRequestHeaders.Add("Origin", "baseUrl");
//client.DefaultRequestHeaders.Add("Referer", "api.fintables.com");
//client.DefaultRequestHeaders.Add("Content-Type", "application/x-www-form-urlencoded");
//client.DefaultRequestHeaders.Add("Accept", "application/json");
//client.DefaultRequestHeaders.Add("If-Modified-Since", "Sat, 1 Jan 2000 00:00:00 GMT");
//client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.0)");
//client.DefaultRequestHeaders.Add("Content-type", "application/x-www-form-urlencoded");
//client.DefaultRequestHeaders.Add("referer", "http://www.tefas.gov.tr/TarihselVeriler.aspx");
//baseUrl            = "https://www.tefas.gov.tr"
//historyEndpoint = "https://www.tefas.gov.tr/api/DB/BindHistoryInfo"
//allocationEndpoint = "https://www.tefas.gov.tr/api/DB/BindHistoryAllocation"
//dateFormat = "2006-01-02"
//    chunkSize = 60


//$"https://api.fintables.com/funds/{symbol}/chart/?start_date={DateTimeOffset.FromUnixTimeSeconds(period1).UtcDateTime.ToString("yyyy-MM-dd")}&compare=";
//HttpResponseMessage response = await client.PostAsync(url, new HttpContent

//HttpContent _Body = new StringContent($"fontip=YAT&sfontur=&fonkod={symbol}&fongrup=&bastarih=12.01.2024&bittarih=30.01.2024&fonturkod=&fonunvantip=");

//using System;
//using System.Collections.Generic;
//using System.Net.Http;
//using System.Net.Http.Headers;
//using System.Threading.Tasks;
//using Newtonsoft.Json.Linq;

//public class Program
//{
//    public static async Task Main(string[] args)
//    {
//        string symbol = "yourSymbol"; // Fon kodu (örnek değer)
//        DateTime startDateVal = DateTime.Parse("2021-01-01");
//        DateTime endDateVal = DateTime.Parse("2021-12-31");
//        Dictionary<string, Dictionary<long, double?>> data = new Dictionary<string, Dictionary<long, double?>>();

//        if (symbol.Length == 3 && symbol != "USD" && symbol != "EUR" && symbol != "GBP")
//        {
//            using (var handler = new HttpClientHandler() { UseCookies = false, AllowAutoRedirect = true })
//            using (var client = new HttpClient(handler))
//            {
//                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/58.0.3029.110 Safari/537.3");
//                client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate");
//                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
//                client.DefaultRequestHeaders.Add("Origin", "https://www.tefas.gov.tr");
//                client.DefaultRequestHeaders.Referrer = new Uri("https://www.tefas.gov.tr");

//                DateTime currentStartDate = startDateVal;

//                data[symbol] = new Dictionary<long, double?>();

//                while (currentStartDate < endDateVal)
//                {
//                    DateTime currentEndDate = currentStartDate.AddMonths(3);
//                    if (currentEndDate > endDateVal)
//                    {
//                        currentEndDate = endDateVal;
//                    }

//                    string package = $"fontip=YAT&sfontur=&fonkod={symbol}&fongrup=&bastarih={currentStartDate:dd.MM.yyyy}&bittarih={currentEndDate:dd.MM.yyyy}&fonturkod=&fonunvantip=";
//                    HttpContent _Body = new StringContent(package, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");

//                    var response = await client.PostAsync("https://www.tefas.gov.tr/api/DB/BindHistoryInfo", _Body);

//                    string responseBody = "";
//                    JObject myObject = new JObject();
//                    JArray timestampCloses = new JArray();

//                    try
//                    {
//                        response.EnsureSuccessStatusCode();
//                        responseBody = await response.Content.ReadAsStringAsync();
//                        myObject = JObject.Parse(responseBody);
//                        timestampCloses = (JArray)myObject["data"];
//                    }
//                    catch (HttpRequestException e)
//                    {
//                        Console.WriteLine($"Request error: {e.Message}");
//                    }

//                    for (int i = 0; i < timestampCloses.Count; i++)
//                    {
//                        long timestamp = (long)Math.Floor((decimal)timestampCloses[i]["TARIH"] / 8640000) * 86400;
//                        if (timestamp > new DateTimeOffset(endDateVal).ToUnixTimeSeconds())
//                        {
//                            break;
//                        }
//                        else
//                        {
//                            double? closeValue = timestampCloses[i]["FIYAT"].Type == JTokenType.Null ? (double?)null : (double)timestampCloses[i]["FIYAT"];
//                            if (!data[symbol].ContainsKey(timestamp))
//                            {
//                                data[symbol][timestamp] = closeValue;
//                            }
//                        }
//                    }

//                    currentStartDate = currentEndDate;
//                }
//            }
//        }

//        // Sonuçları yazdırma (isteğe bağlı)
//        foreach (var entry in data[symbol])
//        {
//            Console.WriteLine($"Timestamp: {entry.Key}, Close Value: {entry.Value}");
//        }
//    }
//}
