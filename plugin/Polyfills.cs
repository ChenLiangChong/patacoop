// The shared server source uses C# 11 'required' members; net6.0 lacks the attribute types.
namespace System.Runtime.CompilerServices
{
    internal sealed class RequiredMemberAttribute : Attribute { }
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) { }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
