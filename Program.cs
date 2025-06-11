using Newtonsoft.Json;
using RoSharp;
using RoSharp.API.Communities;
using RoSharp.Exceptions;
using System.IO.Compression;

string basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GroupWallScrape");
RoLogger.DisableAllLogging();

// Comment = TODO!
// || Do on non-burner: 3514227

// Create base directory
if (!Directory.Exists(basePath))
{
    Directory.CreateDirectory(basePath);
    Console.WriteLine($"Created base directory at: {basePath}.");
}

// Create "all done" folder
string allDonePath = Path.Combine(basePath, "AllDone");
if (!Directory.Exists(allDonePath))
    Directory.CreateDirectory(allDonePath);

// Create "unable to access" folder
string unableToAccessPath = Path.Combine(basePath, "UnableToAccess");
if (!Directory.Exists(unableToAccessPath))
    Directory.CreateDirectory(unableToAccessPath);

// Create targets.txt
string listPath = Path.Combine(basePath, "targets.txt");
if (!File.Exists(listPath))
{
    var writer = File.CreateText(listPath);
    writer.Write("<PLEASE REPLACE WITH A LIST OF GROUP IDS SEPARATED BY COMMAS>");
    writer.Close();
    Console.WriteLine($"target.txt has been created at the following location: {listPath}. Please replace text file contents with a list of group IDs to scrape, separated by commas, eg: 1,2,777,etc");
}

// Create cookie.txt
string cookiePath = Path.Combine(basePath, "cookie.txt");
if (!File.Exists(cookiePath))
{
    var writer = File.CreateText(cookiePath);
    writer.Write("<PLEASE REPLACE WITH .ROBLOSECURITY TOKEN TO USE FOR GROUP WALL REQUESTS>");
    writer.Close();
    Console.WriteLine($"cookie.txt has been created at the following location: {cookiePath}. Please replace text file contents with a suitable .ROBLOSECURITY token to use for requests.");
    return;
}

// Read cookie.txt
string cookieValue = File.ReadAllText(cookiePath);
if (string.IsNullOrWhiteSpace(cookieValue) || cookieValue == "<PLEASE REPLACE WITH .ROBLOSECURITY TOKEN TO USE FOR GROUP WALL REQUESTS>")
{
    Console.WriteLine($"Invalid .ROBLOSECURITY token provided. Please replace the contents of {cookiePath} file with a suitable .ROBLOSECURITY token to use.");
    return;
}

// Read targets.txt
List<int> list = [];
string listText = File.ReadAllText(listPath);
var listTextDashSplit = listText.Split("-");
if (listTextDashSplit.Count() == 2)
{
    if (int.TryParse(listTextDashSplit[0].Trim(), out int start) && int.TryParse(listTextDashSplit[1].Trim(), out int end))
    {
        if (start > end)
        {
            Console.WriteLine($"Oops! Your range of groups in the targets.txt is invalid! The ending group cannot be smaller than the starting group!");
            return;
        }
        list = Enumerable.Range(start, (end - start) + 1).ToList();
        Console.WriteLine($"Range of groups added: Group {start} to group {end}");
    }
    else
    {
        Console.WriteLine($"Oops! Your range of groups in the targets.txt file does not have valid numbers!");
        return;
    }
}
else
{
    foreach (string id in listText.Split(','))
    {
        if (int.TryParse(id.Trim(), out int res))
        {
            Console.WriteLine($"Adding group to request queue: {id}");
            list.Add(res);
        }
        else
        {
            Console.WriteLine($"Skipping list item '{id}'. Invalid numerical input.");
        }
    }
}
if (list.Count == 0)
{
    Console.WriteLine("No valid target IDs were provided. Aborting program.");
    return;
}

Console.WriteLine($"Beginning scraping program with {list.Count} queued groups. First group: {list.First()} | Last group: {list.Last()}");

// Authenticate
Session s = new();
await s.LoginAsync(cookieValue.Trim());

Console.WriteLine("Successfully authenticated as: " + s.AuthUser.Username);

// Scrape
foreach (ulong iterator in list)
{
    Console.WriteLine("Beginning group " + iterator);
    Community c;

    if (File.Exists(Path.Combine(allDonePath, $"GROUP {iterator}.zip")))
    {
        Console.WriteLine($"Group {iterator} has already been scraped! Skipping...");
        continue;
    }

    string dirPath = Path.Combine(basePath, $"GROUP {iterator}-DO NOT MODIFY");
    Directory.CreateDirectory(dirPath);

    bool success = true;
    string info_path = Path.Combine(dirPath, "group-info.json");
    string textFileContents = string.Empty;

    try
    {
        c = await Community.FromId(iterator, s);

        object body = new
        {
            ScrapeDate = DateTime.UtcNow.ToString(),
            GroupName = c.Name,
            Id = c.Id.ToString(),
            Owner = new
            {
                Id = c.Owner?.Id.ToString() ?? "None",
                Username = c.Owner?.Username ?? "None",
            },
            Icon = (await c.GetIconAsync()).Value,
            Description = c.Description,
        };
        Console.WriteLine($"Group name: {c.Name}");

        File.WriteAllText(info_path, JsonConvert.SerializeObject(body));
    }
    catch (RobloxAPIException e)
    {
        Console.WriteLine($"Unable to access wall for group {iterator}. Skipping");
        File.WriteAllText(Path.Combine(dirPath, $"details.txt"), e.ToString());
        success = false;
        goto end;
    }


    int pageCounter = 1;
    string? cursor = "";
    while (cursor != null)
    {
    retry:
        Console.WriteLine($"Page {pageCounter}");
        try
        {
            var data = await c.GetPostsAsync(cursor: cursor);

            // Create json file
            File.WriteAllText(Path.Combine(dirPath, $"{pageCounter}-{(cursor == "" ? "NO_CURSOR" : cursor)}.json"), await data.HttpResponse.Content.ReadAsStringAsync());

            // Add to .txt contents
            foreach (var comment in data.Value)
            {
                textFileContents += $"{comment.PostedAt} | {(comment.PosterId?.UniqueId.ToString() ?? "Unknown")} | {comment.Text}\n";
            }

            // Specify cursor
            cursor = data.Value.NextPageCursor;
            await Task.Delay(1500);
        }
        catch (RobloxAPIException exp)
        {
            if (exp.IsTooManyRequests)
            {
                int retryIn = exp.RetryIn ?? 20;
                if (retryIn >= 58)
                {
                    Console.WriteLine($"Too many requests, high retry timer ({retryIn}s). Re-trying in 5 seconds instead.");
                    await Task.Delay(5000);
                    goto retry;
                }

                Console.WriteLine($"Too many requests, trying again in {retryIn} seconds.");
                await Task.Delay((retryIn * 1000) + 1);
                goto retry;
            }
            else if (exp.Code == System.Net.HttpStatusCode.BadRequest)
            {
                success = false;

                Console.WriteLine($"Group is locked. Skipping.");
                File.WriteAllText(Path.Combine(dirPath, $"details.txt"), exp.ToString());
                goto end;
            }
            else
            {
                if (cursor == "" && exp.Code == System.Net.HttpStatusCode.Forbidden)
                {
                    success = false;

                    Console.WriteLine($"Group wall is hidden. Skipping.");
                    File.WriteAllText(Path.Combine(dirPath, $"details.txt"), exp.ToString());
                    goto end;
                }
                else
                {
                    Console.WriteLine($"Error {exp.Code}. Retrying in 3s... Full message: {exp.Message}");
                    await Task.Delay(3000);
                    goto retry;
                }
            }
        }
        catch (Exception exp)
        {
            Console.WriteLine($"Unknown error. Retrying in 3s... Full message: {exp.Message}");
            await Task.Delay(3000);
            goto retry;
        }
        pageCounter++;
    }

end:
    // Txt
    Console.WriteLine("Creating .txt file...");
    File.WriteAllText(Path.Combine(dirPath, $"{iterator}.txt"), textFileContents);

    // Finish
    Console.WriteLine($"Done. Zipping file...");
    string completedPath = Path.Combine(success ? allDonePath : unableToAccessPath, (success ? string.Empty : "ERROR-") + $"GROUP {iterator}.zip");
    
    // Delete the old file if it exists
    if (File.Exists(completedPath))
    {
        File.Delete(completedPath);
        await Task.Delay(1000);
    }

    ZipFile.CreateFromDirectory(dirPath, completedPath);
    Console.WriteLine($"Done with group {iterator}!\n-----------------\n");

    //iterator++;

    await Task.Delay(3000);
    Directory.Delete(dirPath, true);
}

Console.WriteLine("Scrape has been completed! Press any key to terminate program.");
Console.ReadKey();