namespace PrivateGreeting;
public static class Words
{
    public static string Message(string greeting) => greeting + " · 来自插件私有依赖";
}
