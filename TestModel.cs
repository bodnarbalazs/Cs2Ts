using System;

namespace Cs2Ts;

[ConvertToTs]
public class TestModel
{
    public string Name { get; set; }
    public int Age { get; set; }
    public DateTime? BirthDate { get; set; }
    public bool? IsActive { get; set; }
}
