using System.Text;
using Veil.Services.Terminal;

namespace Veil.Tests;

[TestClass]
public sealed class TerminalStreamDecoderTests
{
    [TestMethod]
    public void Decode_strips_csi_sequences_and_requests_clear()
    {
        var decoder = new TerminalStreamDecoder();
        byte[] payload = Encoding.UTF8.GetBytes("\u001b[2J\u001b[31mhello\u001b[0m");

        TerminalDecodedChunk result = decoder.Decode(payload);

        Assert.IsTrue(result.ClearRequested);
        Assert.AreEqual("hello", result.Text);
        Assert.IsNull(result.Title);
    }

    [TestMethod]
    public void Decode_extracts_terminal_title_from_osc_sequence()
    {
        var decoder = new TerminalStreamDecoder();
        byte[] payload = Encoding.UTF8.GetBytes("\u001b]0;WSL: Ubuntu\u0007prompt");

        TerminalDecodedChunk result = decoder.Decode(payload);

        Assert.AreEqual("WSL: Ubuntu", result.Title);
        Assert.AreEqual("prompt", result.Text);
        Assert.IsFalse(result.ClearRequested);
    }
}
