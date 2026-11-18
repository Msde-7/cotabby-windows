using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference.Tests;

/// <summary>A test that loads the GGUF named by COTABBY_TEST_MODEL_PATH, skipped when it is unset.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public const string Variable = "COTABBY_TEST_MODEL_PATH";

    public ModelFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a GGUF model to run model-backed tests.";
        }
    }
}

/// <summary>
/// Model-backed checks that a reused prompt, including a recurrent or hybrid model's restored
/// checkpoint, generates exactly what a fresh context generates. Runs on the CPU backend with
/// one-token micro-batches, so both paths compute every token with the same kernels and any
/// difference is a state bug rather than batch-size rounding.
/// </summary>
public sealed partial class LlamaRuntimeCoreModelTests : IDisposable
{
    private static readonly LlamaGenerationOptions Options = new() { MaxTokens = 10 };

    private const string Text =
        "Cotabby adds autocomplete to almost any text field on your computer. As you type, a gray "
        + "suggestion appears next to your cursor. Press Tab to accept it a word at a time, or keep "
        + "typing to ignore it. The default engine runs a small model on your own machine, and";

    private readonly ModelParams _parameters;
    private readonly LLamaWeights _weights;

    public LlamaRuntimeCoreModelTests()
    {
        // Only constructed for a test that is not skipped, so the variable is set.
        var path = Environment.GetEnvironmentVariable(ModelFactAttribute.Variable)!;
        UseCpuBackend();
        _parameters = new ModelParams(path)
        {
            ContextSize = 1024,
            GpuLayerCount = 0,
            Threads = 4,
            BatchThreads = 4,
            BatchSize = 512,
            UBatchSize = 1,
        };
        _weights = LLamaWeights.LoadFromFile(_parameters);
    }

    [ModelFact]
    public async Task ReusedPromptsGenerateWhatAFreshContextGenerates()
    {
        var log = new RecordingLogger();
        using var core = NewCore(log);

        string[] prompts =
        [
            Text,
            Text + " the",           // typing forward
            Text + " the m",
            Text + " th",            // backspacing within the tail
            Text + " the model",
            Text,                    // the same prompt again
            "My " + Text[3..],       // an edit at the start
        ];
        int reused = 0;
        foreach (var prompt in prompts)
        {
            string warm = await core.GenerateAsync(prompt, Options, CancellationToken.None);
            if (log.LastReuse > 0) reused++;
            Assert.Equal(await FreshAsync(prompt), warm);
        }
        // Everything after the first prompt shares its head, except the edit at the start.
        Assert.Equal(prompts.Length - 2, reused);
    }

    [ModelFact]
    public async Task APromptCancelledMidEvaluationLeavesReusableMemory()
    {
        var log = new RecordingLogger();
        using var core = NewCore(log);
        await core.GenerateAsync(Text, Options, CancellationToken.None);

        string longer = Text + " no account or telemetry is needed. An optional engine can connect to a server you set up";
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try { await core.GenerateAsync(longer, Options, cts.Token); }
            catch (OperationCanceledException) { }
        }

        string next = longer + " yourself";
        string warm = await core.GenerateAsync(next, Options, CancellationToken.None);
        Assert.True(log.LastReuse > 0);
        Assert.Equal(await FreshAsync(next), warm);
    }

    [ModelFact]
    public async Task AGenerationCutShortByDisposalReturnsNothing()
    {
        var core = NewCore(new RecordingLogger());
        var firstPartial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool disposing = false;
        var options = new LlamaGenerationOptions
        {
            MaxTokens = 200,
            OnPartial = _ =>
            {
                if (!firstPartial.TrySetResult()) return;
                // Holds the decode until disposal has cancelled it, so it cannot finish first.
                SpinWait.SpinUntil(() => Volatile.Read(ref disposing), TimeSpan.FromSeconds(10));
                Thread.Sleep(200);
            },
        };

        var generation = core.GenerateAsync(Text, options, CancellationToken.None);
        await firstPartial.Task;
        Volatile.Write(ref disposing, true);
        core.Dispose();

        // An unload mid-decode must not hand back the fragment decoded so far as a suggestion.
        Assert.Equal(string.Empty, await generation);
    }

    private LlamaRuntimeCore NewCore(ILogger logger) =>
        new(_weights, _weights.CreateContext(_parameters), logger);

    private async Task<string> FreshAsync(string prompt)
    {
        using var core = NewCore(new RecordingLogger());
        return await core.GenerateAsync(prompt, Options, CancellationToken.None);
    }

    /// <summary>Loads llama.cpp's CPU build, so the test never touches a GPU another process uses.</summary>
    private static void UseCpuBackend()
    {
        string level = Avx512F.IsSupported ? "avx512" : Avx2.IsSupported ? "avx2" : Avx.IsSupported ? "avx" : "noavx";
        string dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", level);
        try
        {
            NativeLibraryConfig.All.WithLibrary(Path.Combine(dir, "llama.dll"), Path.Combine(dir, "mtmd.dll"));
        }
        catch (InvalidOperationException)
        {
            // Already loaded by an earlier test in this process.
        }
    }

    public void Dispose() => _weights.Dispose();

    private sealed partial class RecordingLogger : ILogger
    {
        public int LastReuse { get; private set; } = -1;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var match = ReusePattern().Match(formatter(state, exception));
            if (match.Success) LastReuse = int.Parse(match.Groups[1].Value);
        }

        [GeneratedRegex(@"reuse (\d+)/\d+")]
        private static partial Regex ReusePattern();
    }
}
