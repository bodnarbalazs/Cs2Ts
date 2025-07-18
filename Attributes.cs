namespace Cs2Ts;

[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct | System.AttributeTargets.Enum | System.AttributeTargets.Interface)]
public sealed class ConvertToTsAttribute : System.Attribute
{
}

[System.AttributeUsage(System.AttributeTargets.Property)]
public sealed class ReactNodeAttribute : System.Attribute
{
}

[System.AttributeUsage(System.AttributeTargets.Property)]
public sealed class HtmlElementAttribute : System.Attribute
{
}
