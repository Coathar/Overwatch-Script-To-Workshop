using System.Text;
using Deltin.Deltinteger.Elements;
using Deltin.Deltinteger.WorkshopWiki;
using Deltin.Deltinteger.Parse;
using Deltin.Deltinteger.LanguageServer;
using CompletionItem = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItem;
using CompletionItemKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItemKind;
using SignatureInformation = OmniSharp.Extensions.LanguageServer.Protocol.Models.SignatureInformation;
using StringOrMarkupContent = OmniSharp.Extensions.LanguageServer.Protocol.Models.StringOrMarkupContent;

namespace Deltin.Deltinteger
{
    public interface IWorkshopTree
    {
        string ToWorkshop(OutputLanguage language);
    }

    public interface IMethod : IScopeable, IParameterCallable
    {
        CodeType ReturnType { get; }
        IWorkshopTree Parse(ActionSet actionSet, IWorkshopTree[] values, object[] additionalParameterData);
        bool DoesReturnValue();
    }

    public interface ISkip
    {
        int SkipParameterIndex();
    }

    public interface INamed
    {
        string Name { get; }
    }

    public interface IScopeable : INamed
    {
        AccessLevel AccessLevel { get; }
        Location DefinedAt { get; }
        bool WholeContext { get; }
        CompletionItem GetCompletion();
    }

    public interface ICallable : INamed
    {
        void Call(ScriptFile script, DocRange callRange);
    }

    public interface IParameterCallable : ILabeled
    {
        CodeParameter[] Parameters { get; }
        Location DefinedAt { get; }
        AccessLevel AccessLevel { get; }
        StringOrMarkupContent Documentation { get; }
    }

    public interface IGettable
    {
        IWorkshopTree GetVariable(Element eventPlayer = null);
    }

    public interface IIndexReferencer : IScopeable, IExpression, ICallable
    {
        bool Settable();
        VariableType VariableType { get; }
    }

    public interface ILabeled
    {
        string GetLabel(bool markdown);
    }

    public interface IApplyBlock : ILabeled
    {
        void SetupBlock();
        CallInfo CallInfo { get; }
    }
}