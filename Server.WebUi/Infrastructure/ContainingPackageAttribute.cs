using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Hearty.Server.WebUi.Infrastructure;


/// <summary>
/// Provides information on the NuGet package that contains this assembly.
/// </summary>
/// <remarks>
/// This information is needed to output the correct 
/// URLs to web assets that ship with the package.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
internal sealed class ContainingPackageAttribute : Attribute
{
    /// <summary>
    /// Name (ID) of the NuGet package.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="name">See <paramref name="name"/>. </param>
    public ContainingPackageAttribute(string name) 
    { 
        Name = name; 
    }

    /// <summary>
    /// Get the instance of this attribute for this assembly.
    /// </summary>
    public static ContainingPackageAttribute? Instance
        => Assembly.GetExecutingAssembly()
                   .GetCustomAttribute<ContainingPackageAttribute>();
}
