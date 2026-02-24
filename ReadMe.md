# Cs2Ts

A .NET CLI tool that converts C# classes, records, structs, interfaces, and enums into TypeScript type definitions.

## Installation

```bash
dotnet tool install --global Balazs.Cs2Ts
```

Add the marker attribute to your project (a single empty class is all you need):

```csharp
[System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct
    | System.AttributeTargets.Enum | System.AttributeTargets.Interface)]
public sealed class ConvertToTsAttribute : System.Attribute { }
```

## Usage

Navigate to your C# project directory and run:

```bash
cs2ts <outputFolder>
```

For example:

```bash
cs2ts "../../frontend/src/generated"
```

The tool scans all `.cs` files in the current directory (excluding `bin/` and `obj/`) and generates corresponding `.ts` files for types decorated with `[ConvertToTs]`.

## Example

### Input

```csharp
/// <summary>
/// Represents a user in the system.
/// </summary>
[ConvertToTs]
public class User
{
    /// <summary>
    /// Display name shown in the UI.
    /// </summary>
    public string DisplayName { get; set; }
    public int Age { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<string> Roles { get; set; }
    public Dictionary<string, string> Metadata { get; set; }
    public Guid Id { get; set; }
}
```

### Output

```typescript
/**
 * Represents a user in the system.
 */
export interface User {
    /** Display name shown in the UI. */
    displayName: string;
    age: number;
    email: string | null;
    isActive: boolean;
    createdAt: Date;
    roles: string[];
    metadata: Record<string, string>;
    id: string;
}
```

## Attributes

- `[ConvertToTs]` — Marks a class, struct, record, interface, or enum for TypeScript generation
- `[ReactNode]` — Maps a property type to `ReactNode`
- `[HtmlElement]` — Maps a property type to `HTMLElement`

## Type Mappings

| C# | TypeScript |
|---|---|
| `string`, `Guid` | `string` |
| `int`, `long`, `float`, `double`, `decimal` | `number` |
| `bool` | `boolean` |
| `DateTime` | `Date` |
| `DateTimeOffset`, `DateOnly` | `string` |
| `TimeSpan` | `number` |
| `object` | `unknown` |
| `List<T>`, `IEnumerable<T>`, arrays | `T[]` |
| `Dictionary<K, V>` | `Record<K, V>` |
| `T?` | `T \| null` |

## Flags

- `--help`, `-h` — Show usage information
- `--version`, `-v` — Show the installed version

## How It Works

1. Mark your C# types with the `[ConvertToTs]` attribute
2. Run `cs2ts <outputFolder>` from your project directory
3. The tool parses all `.cs` files using Roslyn and generates TypeScript definitions
4. Properties are converted to camelCase, XML docs become JSDoc comments
5. Inheritance is preserved (`extends` in TypeScript)
6. Static readonly/const fields become `export const` declarations
