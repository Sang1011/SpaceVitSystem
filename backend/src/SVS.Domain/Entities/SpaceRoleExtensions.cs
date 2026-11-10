namespace SVS.Domain.Entities;

public static class SpaceRoleExtensions
{
    /// <summary>
    /// Owner > Editor > Viewer. Giá trị enum nhỏ hơn nghĩa là quyền cao hơn (Owner = 0), nên "đủ quyền" là giá trị nhỏ hơn hoặc bằng.
    /// </summary>
    public static bool AtLeast(this SpaceRole role, SpaceRole minimum) => role <= minimum;
}
