namespace SVS.Application.Common.Interfaces;

public interface IPasswordService
{
    string Hash(string password);

    /// <summary>So khớp mật khẩu với băm đã lưu.</summary>
    bool Verify(string hash, string password);
}
