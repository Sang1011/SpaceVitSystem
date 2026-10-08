namespace SVS.Domain.Entities;

public enum TranscriptionMode
{
    /// <summary>Chỉ chép chữ, không gọi Gemini.</summary>
    Transcribe,
    /// <summary>Chép chữ rồi tóm tắt bằng Gemini.</summary>
    Summarize,
    /// <summary>Chỉ tóm tắt lại từ transcript đã có (không chạy lại STT).</summary>
    Resummarize
}
