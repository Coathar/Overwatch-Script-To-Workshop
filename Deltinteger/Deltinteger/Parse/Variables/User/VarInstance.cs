using System.Linq;
using System.Collections.Generic;
using Deltin.Deltinteger.Compiler;
using Deltin.Deltinteger.Parse.Functions.Builder.Virtual;

namespace Deltin.Deltinteger.Parse
{
    public class VariableInstance : IVariableInstance
    {
        public string Name => Var.Name;
        public CodeType CodeType { get; }
        public bool WholeContext => Var.WholeContext;
        public LanguageServer.Location DefinedAt => Var.DefinedAt;
        public AccessLevel AccessLevel => Var.AccessLevel;
        IVariable IVariableInstance.Provider => Var;
        public MarkupBuilder Documentation { get; set; }
        ICodeTypeSolver IScopeable.CodeType => CodeType;
        public IVariableInstanceAttributes Attributes { get; }

        public Var Var { get; }
        readonly CodeType _definedIn;

        public VariableInstance(Var var, InstanceAnonymousTypeLinker instanceInfo, CodeType definedIn)
        {
            Var = var;
            CodeType = var.CodeType.GetRealType(instanceInfo);
            _definedIn = definedIn;
            Attributes = new VariableInstanceAttributes()
            {
                CanBeSet = var.StoreType != StoreType.None,
                StoreType = var.StoreType,
                UseDefaultVariableAssigner = !var.IsMacro
            };
        }

        public void Call(ParseInfo parseInfo, DocRange callRange)
        {
            IVariableInstance.Call(this, parseInfo, callRange);
            parseInfo.Script.AddDefinitionLink(callRange, Var.DefinedAt);
        }

        public IGettableAssigner GetAssigner(ActionSet actionSet) => CodeType.GetRealType(actionSet?.ThisTypeLinker).GetGettableAssigner(new AssigningAttributes() {
            Name = Var.Name,
            Extended = Var.InExtendedCollection,
            ID = Var.ID,
            IsGlobal = actionSet?.IsGlobal ?? true,
            StoreType = Var.StoreType,
            VariableType = Var.VariableType,
            DefaultValue = Var.InitialValue
        });

        public IWorkshopTree ToWorkshop(ActionSet actionSet)
        {
            if (!Var.IsMacro)
                return actionSet.IndexAssigner.Get(Var).GetVariable();
            else
                return ToMacro(actionSet);
        }

        IWorkshopTree ToMacro(ActionSet actionSet)
        {
            var allMacros = new List<VariableInstanceOption>();
            allMacros.Add(new VariableInstanceOption(this));

            // Get the class relation.
            if (_definedIn != null)
            {
                var relation = actionSet.ToWorkshop.ClassInitializer.RelationFromClassType((ClassType)_definedIn);

                // Extract the virtual functions.
                allMacros.AddRange(relation.ExtractOverridenElements<VariableInstance>(extender => extender.Name == Name)
                    .Select(extender => new VariableInstanceOption(extender)));
            }

            return new MacroContentBuilder(actionSet, allMacros).Value;
        }

        class VariableInstanceOption : IMacroOption
        {
            readonly VariableInstance _variableInstance;
            public VariableInstanceOption(VariableInstance variableInstance) => _variableInstance = variableInstance;
            public ClassType ContainingType() => (ClassType)_variableInstance._definedIn;
            public IWorkshopTree GetValue(ActionSet actionSet) => _variableInstance.Var.InitialValue.Parse(actionSet);
        }
    }
}