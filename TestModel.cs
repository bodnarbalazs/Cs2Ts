using System;

namespace Cs2Ts;

[ConvertToTs]
public class TestModel
{
    public string Name { get; set; }
    public int Age { get; set; }
    public DateTime? BirthDate { get; set; }
    public bool? IsActive { get; set; }
    
    // Test Action and Func types
    public Action SimpleAction { get; set; }
    public Action<string> ActionWithParam { get; set; }
    public Action<string, int> ActionWithTwoParams { get; set; }
    public Func<string> FuncWithReturn { get; set; }
    public Func<int, string> FuncWithParamAndReturn { get; set; }
    public Func<string, int, bool> FuncWithTwoParamsAndReturn { get; set; }
    
    // Test new attributes
    [ReactNode]
    public object ReactContent { get; set; }
    
    [HtmlElement]
    public object DomElement { get; set; }
    
    // Test nullable function types
    public Action? NullableAction { get; set; }
    public Func<string>? NullableFunc { get; set; }
}
