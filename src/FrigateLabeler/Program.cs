using FrigateLabeler;

var cli = new Cli(args);
if (AppConfig.CreateTemplate())
    Console.WriteLine($"Created {AppConfig.FilePath} — replace the placeholder with your Anthropic API key.");
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    return cli.Command switch
    {
        "auth" => Commands.Auth(),
        "doctor" => await Commands.Doctor(cli, cts.Token),
        "cameras" => await Commands.Cameras(cts.Token),
        "queue" => await Commands.Queue(cli, cts.Token),
        "label" => await Commands.Label(cli, cts.Token),
        "serve" or "run" or "review" => await Commands.Serve(cli, cts.Token),
        "submit" => await Commands.Submit(cli, cts.Token),
        "notes" => Commands.Notes(cli),
        "healthcheck" => await Commands.HealthCheck(cli),
        "try" => await Commands.Try(cli, cts.Token),
        "help" or "--help" or "-h" => Commands.Help(),
        _ => Commands.Help(),
    };
}
catch (Exception ex) when (ex is FrigateAuthException or ConfigException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Stopped.");
    return 130;
}

static class Commands
{
    public static int Help()
    {
        Console.WriteLine("""
            frigate-labeler — label Frigate+ images with Claude, review them, and submit

            Usage:
              dotnet run               Open the web app at http://localhost:5178: pick images to label,
                                       review Claude's work, approve and verify. (Same as `serve`.)

            Command-line alternatives:
              label  [options]         Label images without the web app
              submit [options]         Upload reviewed results (skips images with open questions)
              queue  [options]         Count images still to label and estimate the cost
              cameras                  List cameras, their labels, and supported labels not enabled
              notes  --camera NAME     Show that camera's notes file
              auth                     Store your Frigate+ refresh token
              doctor                   Check sign-in, API access, image download and the Claude key
              try --file IMG --labels a,b   Label a local image file (no Frigate+ access needed)

            Options:
              --camera NAME            Only this camera
              --image ID               Only this image (repeatable)
              --filter new|unverified|all   Which images to pull (default: unverified)
              --limit N                Stop after N images (default: 10)
              --out DIR                Results folder (default: ./runs)
              --port N                 Web app port (default: 5178, or FRIGATE_LABELER_PORT)
              --host ADDR              Address to listen on (default: 127.0.0.1, or FRIGATE_LABELER_HOST)
              --no-refine              Skip the per-object tightening pass (cheaper, looser boxes)
              --review-rounds N        Max review rounds per image (default: 3; 0 = no review)
              --concurrency N          Parallel Claude requests (default: 4)
              --model ID               Claude model (default: claude-opus-5-5)
              --force                  Re-label images already labeled / submit over website edits
              --include-flagged        submit: also upload images with open questions
              --no-verify              submit: save boxes without verifying them
              --yes                    submit: don't ask for confirmation
              --verbose                Print Claude's per-object notes

            Claude API key: "anthropicApiKey" in ~/.config/frigate-labeler/config.json, or ANTHROPIC_API_KEY.
            Camera notes:   ~/.config/frigate-labeler/cameras/<camera>.md (also editable on the review page).
            """);
        return 1;
    }

    public static int Auth()
    {
        Console.WriteLine("""
            Paste your Frigate+ refresh token and press Enter.

            To find it: open https://plus.frigate.video while signed in → Developer Tools →
            Application → Local Storage → https://plus.frigate.video → the key ending in
            ".refreshToken". Copy its value.
            """);
        Console.Write("> ");
        var token = ReadSecret();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("No token entered.");
            return 1;
        }
        FrigateAuth.SaveRefreshToken(token);
        Console.WriteLine($"Saved to {AppPaths.AuthFile} (readable only by you). Run `doctor` to check it.");
        return 0;
    }

    public static async Task<int> Doctor(Cli cli, CancellationToken ct)
    {
        using var frigate = new FrigateClient();
        var profile = await frigate.GetProfileAsync(ct);
        Console.WriteLine($"✓ Signed in to Frigate+ (account {profile.Id[..8]}…)");

        var cameras = await frigate.GetCamerasAsync(ct);
        Console.WriteLine($"✓ API access: {cameras.Count} cameras");

        var imageId = cli.Images.FirstOrDefault();
        if (imageId is null)
            await foreach (var img in frigate.ListImagesAsync(null, ImageFilter.All, 1, ct)) { imageId = img.Id; break; }

        if (imageId is not null)
        {
            try
            {
                using var bitmap = ImageTools.Decode(await frigate.DownloadImageAsync(imageId, ct));
                Console.WriteLine($"✓ Image download: {imageId} ({bitmap.Width}x{bitmap.Height})");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
            {
                Console.WriteLine($"✗ Image download failed: {ex.Message}");
            }
        }

        var source = AppConfig.Load().DescribeApiKeySource();
        Console.WriteLine(source.StartsWith("placeholder") || source == "none found"
            ? $"✗ Claude API key: {source} — edit {AppConfig.FilePath}"
            : $"✓ Claude API key: {source}");
        return 0;
    }

    public static async Task<int> Cameras(CancellationToken ct)
    {
        using var frigate = new FrigateClient();
        foreach (var c in await frigate.GetCamerasAsync(ct))
        {
            Console.WriteLine($"{c.Name,-20} {c.NumImages,5} images{(CameraNotes.Load(c.Name) is null ? "" : "   [has notes]")}");
            Console.WriteLine($"    enabled:     {string.Join(", ", c.Labels)}");
            var missing = FrigateLabels.NotEnabled(c.Labels);
            if (missing.Count > 0) Console.WriteLine($"    not enabled: {string.Join(", ", missing)}");
        }
        return 0;
    }

    /// <summary>For Docker's HEALTHCHECK: is the web app answering?</summary>
    public static async Task<int> HealthCheck(Cli cli)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try { return (await http.GetStringAsync($"http://127.0.0.1:{cli.Port}/healthz")) == "ok" ? 0 : 1; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return 1; }
    }

    public static int Notes(Cli cli)
    {
        if (cli.Camera is null)
        {
            Console.Error.WriteLine("notes needs --camera NAME");
            return 1;
        }
        Console.WriteLine($"Notes for {cli.Camera}: {CameraNotes.PathFor(cli.Camera)}");
        Console.WriteLine(CameraNotes.Load(cli.Camera) ?? "(none yet: create that file, or use the review page)");
        return 0;
    }

    public static async Task<int> Queue(Cli cli, CancellationToken ct)
    {
        using var frigate = new FrigateClient();
        var store = new RunStore(cli.OutDir);
        Console.WriteLine("Checking the queue…");
        var queue = await LoadQueue(frigate, store, cli, ct);
        foreach (var g in queue.GroupBy(i => i.Camera).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {g.Key,-20} {g.Count(),5}");
        PrintEstimate(cli, queue.Count, Math.Min(cli.Limit, queue.Count),
            new CostEstimator(CostEstimator.LoadHistory(cli.OutDir)));
        return 0;
    }

    public static async Task<int> Label(Cli cli, CancellationToken ct)
    {
        using var frigate = new FrigateClient();
        var store = new RunStore(cli.OutDir);
        var costs = new CostEstimator(CostEstimator.LoadHistory(cli.OutDir));
        Console.WriteLine("Checking the queue…");
        var queue = await LoadQueue(frigate, store, cli, ct);
        var images = queue.Take(cli.Limit).ToList();
        PrintEstimate(cli, queue.Count, images.Count, costs);
        if (images.Count == 0) return 0;

        var job = new LabelingJob(frigate, () => CreateLabeler(cli), store, costs, cli.Verbose);
        job.Enqueue(images);
        await job.RunAsync(ct, stopWhenEmpty: true);
        Console.WriteLine($"\nLabeled {job.Processed} image(s) for ${costs.SessionTotal:F2}. Review them with: dotnet run");
        return 0;
    }

    /// <summary>The web app: choose images to label, review the results, approve and verify.</summary>
    public static async Task<int> Serve(Cli cli, CancellationToken ct)
    {
        using var frigate = new FrigateClient();
        var store = new RunStore(cli.OutDir);
        var costs = new CostEstimator(CostEstimator.LoadHistory(cli.OutDir));
        var job = new LabelingJob(frigate, () => CreateLabeler(cli), store, costs, cli.Verbose);
        var server = new ReviewServer(store, frigate, () => CreateLabeler(cli), job, costs);

        var worker = job.RunAsync(ct);
        await server.RunAsync(cli.Host, cli.Port, ct);
        try { await worker; } catch (OperationCanceledException) { }
        return 0;
    }

    public static async Task<int> Submit(Cli cli, CancellationToken ct)
    {
        var store = new RunStore(cli.OutDir);
        var all = store.LoadAll()
            .Where(r => r.SubmittedAt is null)
            .Where(r => cli.Camera is null || r.Camera == cli.Camera)
            .Where(r => cli.Images.Count == 0 || cli.Images.Contains(r.ImageId))
            .ToList();
        // Open questions and results that never went through review don't upload by default.
        var pending = all.Where(r => cli.IncludeFlagged || (!r.NeedsHuman && (r.ReviewRounds > 0 || r.HumanEdited))).ToList();
        if (all.Count > pending.Count)
            Console.WriteLine($"Skipping {all.Count - pending.Count} image(s) with open questions or no review " +
                              "(handle them on the review page, or pass --include-flagged).");
        if (pending.Count == 0)
        {
            Console.WriteLine("Nothing to submit.");
            return 0;
        }

        Console.WriteLine($"Ready to save {pending.Count} image(s) to Frigate+{(cli.Verify ? " and verify them" : " (save only, no verify)")}:");
        foreach (var run in pending)
            Console.WriteLine($"  {run.Camera}/{run.ImageId}: {run.Objects.Count} box(es)");
        if (!cli.Yes && !Confirm("Submit these?")) return 1;

        using var frigate = new FrigateClient();
        var ok = 0;
        foreach (var run in pending)
        {
            var (success, message) = await Submitter.SubmitAsync(frigate, store, run, cli.Force, cli.Verify, ct);
            Console.WriteLine($"  {run.ImageId}: {message}");
            if (success) ok++;
        }
        Console.WriteLine($"Done: {ok} of {pending.Count} submitted.");
        return 0;
    }

    public static async Task<int> Try(Cli cli, CancellationToken ct)
    {
        if (cli.File is null || cli.Labels.Count == 0)
        {
            Console.Error.WriteLine("try needs --file IMAGE and --labels label1,label2,...");
            return 1;
        }
        var jpeg = await File.ReadAllBytesAsync(cli.File, ct);
        var id = Path.GetFileNameWithoutExtension(cli.File);
        var run = await CreateLabeler(cli).LabelAsync(new ImageSummary(id, cli.Camera ?? "local", []), jpeg,
            new ImageData(), cli.Labels, ct);
        var store = new RunStore(cli.OutDir);
        store.Save(run, jpeg);

        foreach (var (o, i) in run.Objects.Select((o, i) => (o, i)))
            Console.WriteLine($"{i + 1,2}. {o.Label,-14} [{o.Box.X1:F0},{o.Box.Y1:F0},{o.Box.X2:F0},{o.Box.Y2:F0}]" +
                              $"{(o.Difficult ? " difficult" : "")}{(o.Note is null ? "" : "  — " + o.Note)}");
        foreach (var issue in run.Issues) Console.WriteLine($"? {issue}");
        Console.WriteLine($"Overlay: {store.OverlayPath(run.Camera, id)}   (${run.CostUsd:F3})");
        return 0;
    }

    // ---------------- helpers ----------------

    private static ClaudeLabeler CreateLabeler(Cli cli) =>
        new(AppConfig.Load().CreateAnthropicClient(), new LabelerOptions
        {
            Model = cli.Model,
            Refine = cli.Refine,
            ReviewRounds = cli.ReviewRounds,
            MaxConcurrency = cli.Concurrency,
            Guidelines = LoadGuidelines(),
            DebugDir = Environment.GetEnvironmentVariable("FRIGATE_LABELER_DEBUG"),
        });

    /// <summary>Images matching the filters that don't have a result yet (all of them with --force).</summary>
    private static async Task<List<ImageSummary>> LoadQueue(FrigateClient frigate, RunStore store, Cli cli,
        CancellationToken ct)
    {
        var queue = new List<ImageSummary>();
        await foreach (var image in SelectImages(frigate, cli, ct))
            if (cli.Force || !store.Exists(image.Camera, image.Id))
                queue.Add(image);
        return queue;
    }

    private static async IAsyncEnumerable<ImageSummary> SelectImages(FrigateClient frigate, Cli cli,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (cli.Images.Count > 0)
        {
            foreach (var id in cli.Images) yield return await frigate.GetImageAsync(id, ct);
            yield break;
        }
        // The unverified filter is per camera (it lists that camera's labels), so walk every camera.
        var cameras = cli.Filter == ImageFilter.Unverified && cli.Camera is null
            ? (await frigate.GetCamerasAsync(ct)).Select(c => (string?)c.Name).ToList()
            : [cli.Camera];
        foreach (var camera in cameras)
            await foreach (var img in frigate.ListImagesAsync(camera, cli.Filter, ct: ct))
                yield return img;
    }

    private static void PrintEstimate(Cli cli, int queueCount, int runCount, CostEstimator costs)
    {
        var scope = cli.Images.Count > 0 ? "selected" : $"{cli.Filter.ToString().ToLowerInvariant()}" +
                                                       (cli.Camera is null ? "" : $" {cli.Camera}");
        Console.WriteLine($"Queue: {queueCount} unlabeled {scope} image(s). This run: {runCount} (--limit {cli.Limit}).");
        Console.WriteLine(costs.Average is { } avg
            ? $"Estimated cost: this run {costs.Estimate(runCount)}, whole queue {costs.Estimate(queueCount)}" +
              $"  (avg ${avg:F3}/image over {costs.SampleCount} labeled image(s))"
            : "Estimated cost: unknown until the first image is labeled.");
    }

    private static string LoadGuidelines()
    {
        // A copy in the working directory wins, so you can tune it without rebuilding.
        foreach (var path in new[] { "guidelines.md", Path.Combine(AppContext.BaseDirectory, "prompts", "guidelines.md") })
            if (File.Exists(path)) return File.ReadAllText(path);
        throw new FileNotFoundException("prompts/guidelines.md not found.");
    }

    private static bool Confirm(string question)
    {
        Console.Write($"{question} [y/N] ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.In.ReadToEnd().Trim();
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            chars.Add(key.KeyChar);
        }
        Console.WriteLine();
        return new string(chars.ToArray()).Trim();
    }
}

sealed class Cli
{
    public string Command { get; }
    public string? Camera { get; private set; }
    public List<string> Images { get; } = [];
    public ImageFilter Filter { get; private set; } = ImageFilter.Unverified;
    public bool Verify { get; private set; } = true;
    public int Limit { get; private set; } = 10;
    public string OutDir { get; private set; } = AppPaths.DefaultDataDir;
    public string Host { get; private set; } = Environment.GetEnvironmentVariable("FRIGATE_LABELER_HOST") ?? "127.0.0.1";
    public int Port { get; private set; } = int.TryParse(Environment.GetEnvironmentVariable("FRIGATE_LABELER_PORT"), out var p) ? p : 5178;
    public bool Refine { get; private set; } = true;
    public int Concurrency { get; private set; } = 4;
    public string Model { get; private set; } = "claude-opus-5-5";
    public bool Force { get; private set; }
    public bool Yes { get; private set; }
    public int ReviewRounds { get; private set; } = 3;
    public bool IncludeFlagged { get; private set; }
    public bool Verbose { get; private set; }
    public string? File { get; private set; }
    public List<string> Labels { get; private set; } = [];

    public Cli(string[] args)
    {
        Command = args.FirstOrDefault() ?? "serve";
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--camera": Camera = Next(); break;
                case "--image": Images.Add(Next()); break;
                case "--filter": Filter = Enum.Parse<ImageFilter>(Next(), ignoreCase: true); break;
                case "--limit": Limit = int.Parse(Next()); break;
                case "--out": OutDir = Next(); break;
                case "--port": Port = int.Parse(Next()); break;
                case "--host": Host = Next(); break;
                case "--no-refine": Refine = false; break;
                case "--concurrency": Concurrency = int.Parse(Next()); break;
                case "--model": Model = Next(); break;
                case "--force": Force = true; break;
                case "--yes": Yes = true; break;
                case "--review-rounds": ReviewRounds = int.Parse(Next()); break;
                case "--include-flagged": IncludeFlagged = true; break;
                case "--no-verify": Verify = false; break;
                case "--verbose": Verbose = true; break;
                case "--file": File = Next(); break;
                case "--labels": Labels = Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
    }
}
