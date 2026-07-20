// netstandard2.1 には C# 9+ の init アクセサ / required メンバに必要なランタイム型が無いため、
// ここでポリフィルとして定義する。Unity(netstandard2.1)側でも同エンジンをそのまま共有できる。

namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>init アクセサ用のマーカー型。</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }

    /// <summary>required メンバ用のマーカー属性。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    /// <summary>コンパイラ機能要求属性（required の実装に必要）。</summary>
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>required メンバをすべて設定するコンストラクタを示す属性。</summary>
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}
