using System.Text;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class LogWatcherAlerts
{
    private static object lockObject = new object();
    private static long lastPosition = 0;
    private static IConfiguration _configuration;

    private static void SaveLastPosition() => File.WriteAllText("lastPosition.txt", lastPosition.ToString());
    private static void LogMessage(string message) => Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    private static void LoadLastPosition()
    {
        if (File.Exists("lastPosition.txt"))
        {
            string content = File.ReadAllText("lastPosition.txt");
            long.TryParse(content, out lastPosition);
        }
    }

    static void Main()
    {
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .Build();

        LoadLastPosition();

        string path = _configuration.GetSection("AppSettings")["LogFilePath"];
        FileSystemWatcher watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path));
        Console.WriteLine($"Observando el archivo: {path}");

        watcher.Changed += OnChanged;
        watcher.EnableRaisingEvents = true;

        LogMessage("Programa iniciado. Presiona enter para salir");
        Console.ReadLine();

        SaveLastPosition();
    }

    private static async void OnChanged(object sender, FileSystemEventArgs e)
    {
        List<string> alertMessages = new List<string>();

        lock (lockObject)
        {
            using (FileStream stream = new FileStream(e.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(lastPosition, SeekOrigin.Begin);

                using (StreamReader reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Contains("Server log: [ALERTA]"))
                        {
                            try
                            {
                                JObject logEntry = JObject.Parse(line);
                                string message = logEntry["message"]?.ToString() ?? "";
                                alertMessages.Add(message.Replace("[ALERTA]", ""));
                            }
                            catch (JsonReaderException ex)
                            {
                                LogMessage($"Error al parsear JSON: {ex.Message}");
                            }
                        }
                    }
                    lastPosition = stream.Position;
                }
            }
        }
        SaveLastPosition();
        foreach (var alert in alertMessages)
        {
            await ProcessAlert(alert);
        }
    }

    private static async Task ProcessAlert(string alert)
    {
        string[] messages = alert.Split(';');
        string summary = $"{messages[1]} en {messages[2]}"; //Añadimos el host.name
        string description = messages[7];

        // JSON para Jira
        JObject jiraData = new JObject(
            new JProperty("fields", new JObject(
                new JProperty("project", new JObject(new JProperty("key", "AL"))),
                new JProperty("summary", summary),
                new JProperty("description", description),
                new JProperty("issuetype", new JObject(new JProperty("name", "Alerta")))
            ))
        );

        //Enviamos petición
        using (HttpClient client = new HttpClient())
        {
            string apiKey = _configuration.GetSection("AppSettings")["ApiKey"];
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes($"joboufra@proton.me:{apiKey}")));

            var response = await client.PostAsync("https://joboufra.atlassian.net/rest/api/2/issue",
                new StringContent(jiraData.ToString(), Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                JObject jsonResponse = JObject.Parse(responseBody);
                string ticketKey = jsonResponse["key"].ToString();
                string ticketUrl = $"https://joboufra.atlassian.net/browse/{ticketKey}";
                LogMessage($"Ticket creado: {ticketKey}. Enlace: {ticketUrl}");
            }
            else
            {
                LogMessage("Error al crear ticket");
            }
        }
    }
}