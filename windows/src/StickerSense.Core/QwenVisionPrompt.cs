using System.Text;

namespace StickerSense.Core;

/// <summary>Chat format of Qwen2.5-VL / Qwen3-VL (ChatML with vision tags).</summary>
public static class QwenVisionPrompt
{
    /// <summary>
    /// One <c>&lt;|vision_start|&gt;&lt;|image_pad|&gt;&lt;|vision_end|&gt;</c> per image (GenAI's processor
    /// expands each pad to the image's tokens), ending with the start of the reply for the model to continue.
    /// </summary>
    public static string Build(string instructions, string prompt, int imageCount, string replyStart = "")
    {
        var b = new StringBuilder();
        b.Append("<|im_start|>system\n").Append(instructions).Append("<|im_end|>\n");
        b.Append("<|im_start|>user\n");
        for (var i = 0; i < imageCount; i++) b.Append("<|vision_start|><|image_pad|><|vision_end|>");
        b.Append(prompt).Append("<|im_end|>\n");
        b.Append("<|im_start|>assistant\n").Append(replyStart);
        return b.ToString();
    }

    /// <summary>
    /// An upper bound for the tokens one image takes: Qwen's vision encoder makes one token per
    /// 32×32 pixels (16-pixel patches, merged 2×2), after rounding each side to a multiple of 32.
    /// </summary>
    public static int MaxImageTokens(int width, int height) => (width / 32 + 1) * (height / 32 + 1);
}
