using System.Diagnostics;
using System.Globalization;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Collections.Immutable;
using Microsoft.VisualBasic;

internal class Program
{
    // options dictionary details in the GetConfig method.
    static Dictionary<string,string> options = new Dictionary<string, string>();
    // Main prices.csv path. it is like a database for prices.
    static string filePath = "Commodity-Prices.csv";
    static string tempFilePath = "Commodity-Prices_Temp.csv";
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
        Console.WriteLine("5. Delete price database lines");
        string? userInput = Console.ReadLine();
        bool exit = false;
        switch (userInput)
        {
            case "":
                exit = true;
                break;
            case "1":
                PriceUpdater().Wait(); // 
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
            case "5":
                Console.WriteLine("please enter delete row count or default 1");
                string input = Console.ReadLine();
                int selectedOption;
                if (int.TryParse(input, out selectedOption))
                {
                    DeletePriceLine(selectedOption);
                }
                else
                {
                    DeletePriceLine(1);
                }
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
            //write default config and read again.
            using(var writer = new StreamWriter(configFilePath))
            {
                writer.WriteLine("StockPostfix:.IS");
                writer.WriteLine("CommodityCurrency:TRY");
            }
            GetConfig();
        }
    }

    static void DeletePriceLine(int? deleteLineCount)
    {
        if (!deleteLineCount.HasValue)
        {
            deleteLineCount = 1;
        }
        var lines = File.ReadAllLines(filePath);
        File.WriteAllLines(tempFilePath, lines.Take(lines.Length - (int)deleteLineCount).ToArray());
        File.Move(tempFilePath, filePath, true);
        File.Delete(tempFilePath);
    }
    static async Task PriceUpdater()
    {
        HttpClient client = new HttpClient();
        var data = new Dictionary<string, Dictionary<long, double?>>();
        DateTime latestDate = DateTime.MinValue;
        bool updateOnlyNewData = false;
        bool appendToFile = false;
        List<string> symbols = new List<string>();
        var symbolsWithDates = new Dictionary<string, string[]>();

        // Check if the file exists and read the latest date
        string[]? headers = null;
        if (System.IO.File.Exists(filePath))
        {
            using (var reader = new StreamReader(filePath))
            {
                // Read header to extract symbols
                try
                { 
                    var headerLine = reader.ReadLine();
                    if (headerLine == null) { throw new NullReferenceException(); };
                    headers = headerLine.Split(';');
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
            //string[] headers = null;
            //using (var reader = new StreamReader(filePath)) { headers = reader.ReadLine().Split(";"); };
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
                if (updateOnlyNewData && latestDate != DateTime.MinValue && headers.Contains(symbol) == true)
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
                            data[symbol] = new Dictionary<long, double?>();
                            for (int i = 0; i < timestampCloses.Count(); i++)
                            {
                                if ((long)Math.Floor((double)timestampCloses[i]["TARIH"] / 86400000) * 86400 > period2)
                                {
                                    i = timestampCloses.Count();
                                }
                                else
                                {
                                    double? closeValue = timestampCloses[i]["FIYAT"].Type == JTokenType.Null ? (double?)null : (double)timestampCloses[i]["FIYAT"];
                                    data[symbol][(long)Math.Floor((double)timestampCloses[i]["TARIH"] / 86400000) * 86400] = closeValue;
                                }
                            }
                        }
                        currentStartDate = currentStartDate.AddMonths(3);
                    }
                }
                else if(symbol != "ZPLIB" && symbol != "ZGOLD")
                {
                    // Send a GET request to the Yahoo Finance API for other symbols
                    string url = "";
                    bool isCurrency = false;
                    if (symbol == "USD" | symbol == "EUR" | symbol == "GBP")
                    {
                        isCurrency = true;
                        url = $"https://query1.finance.yahoo.com/v7/finance/chart/{symbol}TRY=X?period1={period1}&period2={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&interval=1d&events=history&includeAdjustedClose=true";
                    }
                    else
                    {
                        url = $"https://query1.finance.yahoo.com/v7/finance/chart/{symbol}{stockPrefix}?period1={period1}&period2={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}&interval=1d&events=history&includeAdjustedClose=true";
                    }
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
                            //Console.WriteLine((long)timestamps[i]);
                            //Console.WriteLine(DateTimeOffset.FromUnixTimeSeconds((long)timestamps[i]).UtcDateTime);
                            if ((long)(Math.Floor(((double)timestamps[i] + (isCurrency ? 60 * 60 : 0)) / 86400) * 86400 ) > period2)
                            {
                                i = timestamps.Count();
                            }
                            else
                            {
                                double? closeValue = closes[i].Type == JTokenType.Null ? (double?)null : (double)closes[i];
                                data[symbol][(long)(Math.Floor(((double)timestamps[i] + (isCurrency ? 60 * 60 : 0)) / 86400) * 86400)] = closeValue;
                            }
                        }
                    }
                }
                else //ZPLIB ZGOLD and etf zone
                {
                    // Send a GET request to the ... api for funds data.
                    string url = "";
                    bool isCurrency = false;
                    url = $"https://api.fintables.com/funds/{symbol}/chart/?start_date={DateTimeOffset.FromUnixTimeSeconds(period1).UtcDateTime.ToString("yyyy-MM-dd")}&compare=";
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
                            if ((long)Math.Floor((double)((DateTimeOffset)(DateTime.ParseExact(timestampCloses[i]["date"].ToString(),dateFormats,CultureInfo.InvariantCulture))).ToUnixTimeSeconds() / 86400) * 86400 + 86400 > period2)
                            {
                                i = timestampCloses.Count();
                            }
                            else
                            {
                                double? closeValue = timestampCloses[i][$"{symbol}"].Type == JTokenType.Null ? (double?)null : (double)timestampCloses[i][$"{symbol}"];
                                data[symbol][(long)Math.Floor((double)((DateTimeOffset)(DateTime.ParseExact(timestampCloses[i]["date"].ToString(), dateFormats, CultureInfo.InvariantCulture))).ToUnixTimeSeconds() / 86400) * 86400+86400] = closeValue;
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
            if (allTimestamps.Count == 0) { return; };
            using (var writer = new StreamWriter(tempFilePath, append: false))
            using (var reader = new StreamReader(filePath))
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
                    reader.ReadLine();
                }
                else
                {
                    writer.Write(reader.ReadLine());
                    foreach (var symbol in symbols)
                    {
                        if (!headers.Contains(symbol))
                        {
                            writer.Write($";{symbol}");
                        }
                    }
                    writer.WriteLine();
                }
                int i = 0; 
                DateTime firstTimestamp = DateTime.ParseExact(DateTimeOffset.FromUnixTimeSeconds(allTimestamps.ToList()[i]).UtcDateTime.ToString("yyyy-MM-dd"), dateFormats, CultureInfo.InvariantCulture);
                while (i <= (allTimestamps.Count-1)) //because of zero index
                {
                    if (reader.EndOfStream == true)
                    {
                        // errro in here
                        firstTimestamp = DateTime.ParseExact(DateTimeOffset.FromUnixTimeSeconds(allTimestamps.ToList()[i]).UtcDateTime.ToString("yyyy-MM-dd"), dateFormats, CultureInfo.InvariantCulture);
                        writer.Write($"{firstTimestamp.ToShortDateString()}");
                        foreach (var symbol in symbols)
                        {
                            if (data[symbol].TryGetValue(allTimestamps.ToList()[i], out var close))
                            {
                                writer.Write($";{close?.ToString() ?? ""}");
                            }
                            else
                            {
                                writer.Write(";");
                            }
                        }
                        writer.WriteLine();
                        i++;
                    }
                    else
                    {
                        string readedLine = reader.ReadLine();
                        string readedDate = readedLine;
                        readedDate = readedDate.Substring(0, readedDate.IndexOf(";"));
                        if (readedDate == firstTimestamp.ToShortDateString())
                        {
                            writer.Write(readedLine);
                            foreach (var symbol in symbols)
                            {
                                if (data[symbol].TryGetValue(allTimestamps.ToList()[i], out var close) && !headers.Contains(symbol))
                                {
                                    writer.Write($";{close?.ToString() ?? ""}");
                                }
                                else
                                {
                                    //writer.Write(";");
                                }
                            }
                            writer.WriteLine();
                            i++;
                            if (i <= (allTimestamps.Count - 1))
                            {
                                firstTimestamp = DateTime.ParseExact(DateTimeOffset.FromUnixTimeSeconds(allTimestamps.ToList()[i]).UtcDateTime.ToString("yyyy-MM-dd"), dateFormats, CultureInfo.InvariantCulture);
                            }
                        }
                        else
                        {
                            writer.WriteLine(readedLine);
                        }
                    }
                }

                //foreach (var timestamp in allTimestamps)
                //{
                //    var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime.ToString("yyyy-MM-dd");
                //    DateTime parsedDate = DateTime.ParseExact(date, dateFormats, CultureInfo.InvariantCulture);

                //    if (!updateOnlyNewData || parsedDate > latestDate || 1 == 1)
                //    {
                //        writer.Write($"{date}");
                //        foreach (var symbol in symbols)
                //        {
                //            if (!headers.Contains(symbol))
                //            {
                //                //
                //            }
                //            if (data[symbol].TryGetValue(timestamp, out var close))
                //            {
                //                writer.Write($";{close?.ToString() ?? ""}");
                //            }
                //            else
                //            {
                //                writer.Write(";");
                //            }
                //        }
                //        writer.WriteLine();
                //    }
                //    //For new commodities
                //}
            }
            File.Move(tempFilePath, filePath, true);
            File.Delete(tempFilePath);
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
                    if (headers[i] != options["CommodityCurrency"])
                    {
                        writer.WriteLine($"commodity {(headers[i].Any(char.IsDigit) ? "\"" + headers[i] + "\"" : headers[i])} 1.000,00");
                    }
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