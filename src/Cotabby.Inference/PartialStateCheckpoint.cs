using System.Runtime.InteropServices;
using LLama;

namespace Cotabby.Inference;

/// <summary>
/// Where a prompt evaluation on a recurrent or hybrid model starts from, given what memory and the
/// saved checkpoint hold. Pure logic, so the decision is testable without a model.
/// </summary>
public static class PartialStateReuse
{
    /// <summary>
    /// The checkpoint is saved this many prompt tokens before the prompt's end, so the next request
    /// can retokenize or backspace over the last word and still replay at most this many tokens.
    /// </summary>
    public const int TailTokens = 8;

    /// <summary>The largest partial state kept as a checkpoint.</summary>
    public const long MaxCheckpointBytes = 128L * 1024 * 1024;

    /// <summary>
    /// The position at which a prompt evaluation of <paramref name="promptLength"/> tokens that
    /// starts at <paramref name="evaluateFrom"/> saves its checkpoint, or 0 to save none.
    /// </summary>
    /// <param name="existing">The current checkpoint's position, 0 when there is none.</param>
    public static int CheckpointPosition(int evaluateFrom, int promptLength, int existing = 0)
    {
        int at = Math.Max(evaluateFrom, promptLength - TailTokens);
        // Nothing is evaluated. A checkpoint at the very end lies past the prefix the next
        // keystroke shares, which would make that request evaluate the prompt cold.
        if (at >= promptLength) return 0;
        // Evaluation starts within the tail. A checkpoint the shared prefix reaches that is still
        // near the end serves the next keystroke better than one where evaluation starts.
        bool keepsExisting = at == evaluateFrom && existing > 0 && existing <= evaluateFrom
            && existing >= promptLength - 2 * TailTokens;
        return keepsExisting ? 0 : at;
    }

    /// <summary>
    /// The position to evaluate from, and whether the checkpoint must be restored first. Zero means
    /// a cold evaluation.
    /// </summary>
    /// <param name="keep">Prompt tokens shared with the remembered prompt that need no logits.</param>
    /// <param name="remembered">Length of the remembered prompt, whose attention memory is intact.</param>
    /// <param name="memoryExact">
    /// True when the recurrent state is exactly the state after the remembered prompt, false when
    /// generated or half-evaluated tokens have advanced it since.
    /// </param>
    /// <param name="checkpoint">The checkpoint's position, 0 when there is none.</param>
    public static (int From, bool Restore) Plan(int keep, int remembered, bool memoryExact, int checkpoint)
    {
        if (keep <= 0) return (0, false);
        if (memoryExact && keep == remembered) return (keep, false);
        // Attention memory before the checkpoint is the remembered prompt's, so any prompt that
        // shares at least that much continues from the restored state.
        if (checkpoint > 0 && checkpoint <= keep && keep <= remembered) return (checkpoint, true);
        return (0, false);
    }

    /// <summary>True when a partial state of <paramref name="bytes"/> is worth keeping.</summary>
    public static bool FitsCap(long bytes) => bytes > 0 && bytes <= MaxCheckpointBytes;
}

/// <summary>
/// One saved copy of a sequence's partial state: the recurrent state of a recurrent or hybrid
/// model, without the attention memory, which stays in place. Restoring it and dropping the
/// attention memory after its position rewinds the sequence to that position, which llama.cpp
/// cannot do by removing a suffix because a recurrent state has no per-token history.
/// </summary>
/// <remarks>
/// LLamaSharp 0.27 does not wrap llama.cpp's <c>llama_state_seq_*_ext</c> functions, so they are
/// resolved from the llama library LLamaSharp already loaded. Its llama.cpp has only the
/// <c>PARTIAL_ONLY</c> flag, not <c>ON_DEVICE</c>, so the state is copied into host memory. Not
/// thread-safe; the runtime core calls it under its gate.
/// </remarks>
internal sealed class PartialStateCheckpoint
{
    private const uint PartialOnly = 1;

    private readonly NativeFunctions _native;
    private byte[] _buffer = [];

    private PartialStateCheckpoint(NativeFunctions native) => _native = native;

    /// <summary>Position the saved state belongs to, 0 when nothing is saved.</summary>
    public int Position { get; private set; }

    /// <summary>Size of the saved state in bytes, 0 when nothing is saved.</summary>
    public int Bytes => Position > 0 ? _buffer.Length : 0;

    /// <summary>
    /// A checkpoint for <paramref name="weights"/> when its memory cannot drop a suffix, that is
    /// when the model is recurrent or hybrid, and null otherwise or when the native functions are
    /// missing.
    /// </summary>
    public static PartialStateCheckpoint? For(LLamaWeights weights)
    {
        if (NativeFunctions.Load() is not { } native) return null;
        bool recurrent = weights.NativeHandle.IsRecurrent
            || native.ModelIsHybrid(weights.NativeHandle.DangerousGetHandle());
        return recurrent ? new PartialStateCheckpoint(native) : null;
    }

    /// <summary>
    /// Saves the sequence's partial state as the state at <paramref name="position"/>, replacing
    /// the previous checkpoint. On failure, or when the state is over the cap, nothing is saved.
    /// </summary>
    public bool TrySave(LLamaContext context, int sequence, int position)
    {
        Invalidate();
        var ctx = context.NativeHandle.DangerousGetHandle();
        long size = (long)_native.GetSize(ctx, sequence, PartialOnly);
        if (position <= 0 || !PartialStateReuse.FitsCap(size)) return false;
        if (_buffer.Length != size) _buffer = new byte[size];
        if ((long)_native.GetData(ctx, _buffer, (nuint)size, sequence, PartialOnly) != size) return false;
        Position = position;
        return true;
    }

    /// <summary>
    /// Replaces the sequence's partial state with the saved one. The attention memory is left as
    /// it is, so the caller still drops what lies after <see cref="Position"/>.
    /// </summary>
    public bool TryRestore(LLamaContext context, int sequence)
    {
        if (Position <= 0) return false;
        var ctx = context.NativeHandle.DangerousGetHandle();
        if ((long)_native.SetData(ctx, _buffer, (nuint)_buffer.Length, sequence, PartialOnly) == _buffer.Length) return true;
        Invalidate();
        return false;
    }

    public void Invalidate() => Position = 0;

    private sealed class NativeFunctions
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nuint GetSizeExt(IntPtr ctx, int sequence, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nuint GetDataExt(IntPtr ctx, [Out] byte[] destination, nuint size, int sequence, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate nuint SetDataExt(IntPtr ctx, byte[] source, nuint size, int sequence, uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.U1)]
        public delegate bool IsHybrid(IntPtr model);

        public required GetSizeExt GetSize { get; init; }
        public required GetDataExt GetData { get; init; }
        public required SetDataExt SetData { get; init; }
        public required IsHybrid ModelIsHybrid { get; init; }

        /// <summary>
        /// Resolves the functions from the llama library already loaded into the process, so they
        /// run against the same native state as LLamaSharp. Null when any is missing.
        /// </summary>
        public static NativeFunctions? Load()
        {
            var library = GetModuleHandleW("llama.dll");
            if (library == IntPtr.Zero) return null;
            if (!NativeLibrary.TryGetExport(library, "llama_state_seq_get_size_ext", out var getSize)
                || !NativeLibrary.TryGetExport(library, "llama_state_seq_get_data_ext", out var getData)
                || !NativeLibrary.TryGetExport(library, "llama_state_seq_set_data_ext", out var setData)
                || !NativeLibrary.TryGetExport(library, "llama_model_is_hybrid", out var isHybrid))
            {
                return null;
            }
            return new NativeFunctions
            {
                GetSize = Marshal.GetDelegateForFunctionPointer<GetSizeExt>(getSize),
                GetData = Marshal.GetDelegateForFunctionPointer<GetDataExt>(getData),
                SetData = Marshal.GetDelegateForFunctionPointer<SetDataExt>(setData),
                ModelIsHybrid = Marshal.GetDelegateForFunctionPointer<IsHybrid>(isHybrid),
            };
        }

        // Only looks up a module that is already loaded, so it can never load a second copy.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr GetModuleHandleW(string moduleName);
    }
}
