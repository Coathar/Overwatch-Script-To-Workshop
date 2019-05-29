﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using Antlr4;
using Antlr4.Runtime;
using Deltin.Deltinteger;
using Deltin.Deltinteger.Elements;

namespace Deltin.Deltinteger.Parse
{
    public class Parser
    {
        static Log Log = new Log("Parse");

        public static Rule[] ParseText(string text)
        {
            AntlrInputStream inputStream = new AntlrInputStream(text);

            // Lexer
            DeltinScriptLexer lexer = new DeltinScriptLexer(inputStream);
            CommonTokenStream commonTokenStream = new CommonTokenStream(lexer);

            // Parse
            DeltinScriptParser parser = new DeltinScriptParser(commonTokenStream);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(new ErrorListener());

            // Get context
            DeltinScriptParser.RulesetContext context = parser.ruleset();

            //PrintContext(context);
            Log.Write(LogLevel.Verbose, context.ToStringTree(parser));

            Visitor visitor = new Visitor();
            visitor.Visit(context);

            {
                // Get the internal global variable to use.
                if (!Enum.TryParse(context.useGlobalVar().PART().ToString(), out Variable useGlobalVar))
                    throw new SyntaxErrorException("useGlobalVar must be a character.", context.useGlobalVar().start);

                // Get the internal player variable to use.
                if (!Enum.TryParse(context.usePlayerVar().PART().ToString(), out Variable usePlayerVar))
                    throw new SyntaxErrorException("usePlayerVar must be a character.", context.usePlayerVar().start);

                Var.Setup(useGlobalVar, usePlayerVar);
            }

            // Get the defined variables.
            var vardefine = context.vardefine();

            for (int i = 0; i < vardefine.Length; i++)
                // The new var is stored in Var.VarCollection
                new DefinedVar(vardefine[i]);

            // Get the user methods.
            var userMethods = context.user_method();

            for (int i = 0; i < userMethods.Length; i++)
                new UserMethod(userMethods[i]); 

            // Parse the rules.
            var rules = context.ow_rule();
            var compiledRules = new List<Rule>();

            for (int i = 0; i < rules.Length; i++)
            {
                ParseRule parsing = new ParseRule(rules[i]);

                Log.Write(LogLevel.Normal, $"Building rule: {parsing.Rule.Name}");
                parsing.Parse();
                Rule rule = parsing.Rule;

                compiledRules.Add(rule);
            }

            Log.Write(LogLevel.Normal, new ColorMod("Build succeeded.", ConsoleColor.Green));

            // List all variables
            Log.Write(LogLevel.Normal, new ColorMod("Variable Guide:", ConsoleColor.Blue));

            int nameLength = DefinedVar.VarCollection.Max(v => v.Name.Length);

            bool other = false;
            foreach (DefinedVar var in DefinedVar.VarCollection)
            {
                ConsoleColor textcolor = other ? ConsoleColor.White : ConsoleColor.DarkGray;
                other = !other;

                Log.Write(LogLevel.Normal,
                    // Names
                    new ColorMod(var.Name + new string(' ', nameLength - var.Name.Length) + "  ", textcolor),
                    // Variable
                    new ColorMod(
                        (var.IsGlobal ? "global" : "player") 
                        + " " + 
                        var.Variable.ToString() +
                        (var.IsInArray ? $"[{var.Index}]" : "")
                        , textcolor)
                );
            }

            return compiledRules.ToArray();
        }
    }

    class ParseRule
    {
        public Rule Rule { get; private set; }

        private readonly List<Element> Actions = new List<Element>();
        private readonly List<Condition> Conditions = new List<Condition>();

        private DeltinScriptParser.Ow_ruleContext RuleContext;

        private readonly bool IsGlobal;

        //private bool CreateInitialSkip = false;
        //private int SkipCountIndex = -1;

        public ParseRule(DeltinScriptParser.Ow_ruleContext ruleContext)
        {
            Rule = CreateRuleFromContext(ruleContext);
            RuleContext = ruleContext;
            IsGlobal = Rule.RuleEvent == RuleEvent.Ongoing_Global;
        }

        public void Parse()
        {
            // Parse conditions
            ParseConditions();
            
            // Parse actions
            ParseBlock(RuleContext.block(), true);

            Rule.Conditions = Conditions.ToArray();
            Rule.Actions = Actions.ToArray();
        }

        static Rule CreateRuleFromContext(DeltinScriptParser.Ow_ruleContext ruleContext)
        {
            string ruleName = ruleContext.STRINGLITERAL().GetText();
            ruleName = ruleName.Substring(1, ruleName.Length - 2);

            RuleEvent ruleEvent = RuleEvent.Ongoing_Global;
            TeamSelector team = TeamSelector.All;
            PlayerSelector player = PlayerSelector.All;

            {
                var additionalArgs = ruleContext.expr();

                foreach (var arg in additionalArgs)
                {
                    string type = arg.GetText().Split('.').ElementAtOrDefault(0);
                    string name = arg.GetText().Split('.').ElementAtOrDefault(1);

                    if (type == "Event")
                    {
                        if (Enum.TryParse(name, out RuleEvent setEvent))
                            ruleEvent = setEvent;
                        else
                            throw new SyntaxErrorException($"Unknown event type \"{arg.GetText()}\".", arg.start);
                    }
                    else if (type == "Team")
                    {
                        if (Enum.TryParse(name, out TeamSelector setTeam))
                            team = setTeam;
                        else
                            throw new SyntaxErrorException($"Unknown team type \"{arg.GetText()}\".", arg.start);
                    }
                    else if (type == "Player")
                    {
                        if (Enum.TryParse(name, out PlayerSelector setPlayer))
                            player = setPlayer;
                        else
                            throw new SyntaxErrorException($"Unknown player type \"{arg.GetText()}\".", arg.start);
                    }
                    else
                        throw new SyntaxErrorException($"Unknown rule argument \"{arg.GetText()}\".", arg.start);
                }
            }

            return new Rule(ruleName, ruleEvent, team, player);
        }

        void ParseConditions()
        {
            // Get the if contexts
            var conditions = RuleContext.rule_if()?.expr();
            
            if (conditions != null)
                foreach(var expr in conditions)
                {
                    Element parsedIf = ParseExpression(expr);
                    // If the parsed if is a V_Compare, translate it to a condition.
                    // Makes "(value1 == value2) == true" to just "value1 == value2"
                    if (parsedIf is V_Compare)
                        Conditions.Add(
                            new Condition(
                                (Element)parsedIf.ParameterValues[0],
                                (Operators)parsedIf.ParameterValues[1],
                                (Element)parsedIf.ParameterValues[2]
                            )
                        );
                    // If not, just do "parsedIf == true"
                    else
                        Conditions.Add(new Condition(
                            parsedIf, Operators.Equal, new V_True()
                        ));
                }
        }

        Element ParseBlock(DeltinScriptParser.BlockContext blockContext, bool fulfillReturns)
        {
            var statements = blockContext.children
                .Where(v => v is DeltinScriptParser.StatementContext)
                .Cast<DeltinScriptParser.StatementContext>().ToArray();

            for (int i = 0; i < statements.Length; i++)
                ParseStatement(statements[i]);

            return null;
        }

        void ParseStatement(DeltinScriptParser.StatementContext statementContext)
        {
            #region Method
            if (statementContext.GetChild(0) is DeltinScriptParser.MethodContext)
            {
                Actions.Add(ParseMethod(statementContext.GetChild(0) as DeltinScriptParser.MethodContext, false));
                return;
            }
            #endregion

            #region Variable set

            if (statementContext.STATEMENT_OPERATION() != null)
            {
                DefinedVar variable;
                Element target;
                Element index = null;
                string operation = statementContext.STATEMENT_OPERATION().GetText();

                Element value;

                value = ParseExpression(statementContext.expr(1) as DeltinScriptParser.ExprContext);

                /*  Format if the variable has an expression beforehand (sets the target player)
                                 expr(0)           .ChildCount
                                   v                   v
                    Statement (    v                   v          ) | Operation | Set to variable
                               Variable to set (       v         )
                                   ^            expr | . | expr
                                   ^                   ^
                                 expr(0)          .GetChild(1) == '.'                               */
                if (statementContext.expr(0).ChildCount == 3
                    && statementContext.expr(0).GetChild(1).GetText() == ".")
                {
                    /*  Get Variable:  .expr(0)              .expr(1)
                                         v                     v  .expr(1) (if the value to be set is an array)
                        Statement (      v                     v      v    ) | Operation | Set to variable
                                   Variable to set (           v      v  )
                                         ^          expr | . | expr | []
                                         ^           ^
                        Get  Target:  .expr(0)    .expr(0)                                            */

                    variable = DefinedVar.GetVar(statementContext.expr(0).expr(1).GetChild(0).GetText(),
                                                 statementContext.expr(0).expr(1).start);
                    target = ParseExpression(statementContext.expr(0).expr(0));

                    // Get the index if the variable has []
                    var indexExpression = statementContext.expr(0).expr(1).expr(1);
                    if (indexExpression != null)
                        index = ParseExpression(indexExpression);
                }
                else
                {
                    /*               .expr(0)             .expr(1)
                                        v                   v 
                        Statement (     v                   v  ) | Operation | Set to variable
                                   Variable to set (expr) | []
                    */
                    variable = DefinedVar.GetVar(statementContext.expr(0).GetChild(0).GetText(),
                                                 statementContext.expr(0).start);
                    target = new V_EventPlayer();

                    // Get the index if the variable has []
                    var indexExpression = statementContext.expr(0).expr(1);
                    if (indexExpression != null)
                        index = ParseExpression(indexExpression);
                }

                switch (operation)
                {
                    case "+=":
                        value = Element.Part<V_Add>(variable.GetVariable(target, index), value);
                        break;

                    case "-=":
                        value = Element.Part<V_Subtract>(variable.GetVariable(target, index), value);
                        break;

                    case "*=":
                        value = Element.Part<V_Multiply>(variable.GetVariable(target, index), value);
                        break;

                    case "/=":
                        value = Element.Part<V_Divide>(variable.GetVariable(target, index), value);
                        break;

                    case "^=":
                        value = Element.Part<V_RaiseToPower>(variable.GetVariable(target, index), value);
                        break;

                    case "%=":
                        value = Element.Part<V_Modulo>(variable.GetVariable(target, index), value);
                        break;
                }

                Actions.Add(variable.SetVariable(value, target, index));
                return;
            }

            #endregion

            #region for

            if (statementContext.GetChild(0) is DeltinScriptParser.ForContext)
            {
                // The action the for loop starts on.
                // +1 for the counter reset.
                int forActionStartIndex = Actions.Count() + 1;

                // The target array in the for statement.
                Element forArrayElement = ParseExpression(statementContext.@for().expr());

                // Use skipIndex with Get/SetIVarAtIndex to get the bool to determine if the loop is running.
                Var isBoolRunningSkipIf = Var.AssignVar(IsGlobal);
                // Insert the SkipIf at the start of the rule.
                Actions.Insert(0,
                    Element.Part<A_SkipIf>
                    (
                        // Condition
                        isBoolRunningSkipIf.GetVariable(),
                        // Number of actions
                        new V_Number(forActionStartIndex)
                    )
                );

                // Create the for's temporary variable.
                DefinedVar forTempVar = Var.AssignDefinedVar(
                    name    : statementContext.@for().PART().GetText(),
                    isGlobal: IsGlobal,
                    token    : statementContext.@for().start
                    );

                // Reset the counter.
                Actions.Add(forTempVar.SetVariable(new V_Number(0)));

                // Parse the for's block.
                ParseBlock(statementContext.@for().block(), false);

                // Take the variable out of scope.
                forTempVar.OutOfScope();

                // Add the for's finishing elements
                //Actions.Add(SetIVarAtIndex(skipIndex, new V_Number(forActionStartIndex))); // Sets how many variables to skip in the next iteraction.
                Actions.Add(isBoolRunningSkipIf.SetVariable(new V_True())); // Enables the skip.

                Actions.Add(forTempVar.SetVariable( // Indent the index by 1.
                    Element.Part<V_Add>
                    (
                        forTempVar.GetVariable(),
                        new V_Number(1)
                    )
                ));

                Actions.Add(Element.Part<A_Wait>(new V_Number(0.06), WaitBehavior.IgnoreCondition)); // Add the Wait() required by the workshop.
                Actions.Add(Element.Part<A_LoopIf>( // Loop if the for condition is still true.
                    Element.Part<V_Compare>
                    (
                        forTempVar.GetVariable(),
                        Operators.LessThan,
                        Element.Part<V_CountOf>(forArrayElement)
                    )
                ));
                Actions.Add(isBoolRunningSkipIf.SetVariable(new V_False()));
                return;
            }

            #endregion

            #region if

            if (statementContext.GetChild(0) is DeltinScriptParser.IfContext)
            {
                /*
                Syntax after parse:

                If:
                    Skip If (Not (expr))
                    (body)
                    Skip - Only if there is if-else or else statements.
                Else if:
                    Skip If (Not (expr))
                    (body)
                    Skip - Only if there is more if-else or else statements.
                Else:
                    (body)

                */

                // Add if's skip if value.
                A_SkipIf if_SkipIf = new A_SkipIf();
                Actions.Add(if_SkipIf);

                // Parse the if body.
                ParseBlock(statementContext.@if().block(), false);

                // Determines if the "Skip" action after the if block will be created.
                // Only if there is if-else or else statements.
                bool addIfSkip = statementContext.@if().else_if().Count() > 0 || statementContext.@if().@else() != null;

                // Create the inital "SkipIf" action now that we know how long the if's body is.
                // Add one to the body length if a Skip action is going to be added.
                if_SkipIf.ParameterValues = new object[]
                {
                    Element.Part<V_Not>(ParseExpression(statementContext.@if().expr())),
                    new V_Number(Actions.Count - 1 - Actions.IndexOf(if_SkipIf) + (addIfSkip ? 1 : 0))
                };

                // Create the "Skip" dummy action.
                A_Skip if_Skip = new A_Skip();
                if (addIfSkip)
                {
                    Actions.Add(if_Skip);
                }

                // Parse else-ifs
                var skipIfContext = statementContext.@if().else_if();
                A_Skip[] elseif_Skips = new A_Skip[skipIfContext.Length]; // The index where the else if's "Skip" action is.
                for (int i = 0; i < skipIfContext.Length; i++)
                {
                    // Create the dummy action.
                    A_SkipIf elseif_SkipIf = new A_SkipIf();

                    // Parse the else-if body.
                    ParseBlock(skipIfContext[i].block(), false);

                    // Determines if the "Skip" action after the else-if block will be created.
                    // Only if there is additional if-else or else statements.
                    bool addIfElseSkip = i < skipIfContext.Length - 1 || statementContext.@if().@else() != null;

                    // Create the "Skip If" action.
                    elseif_SkipIf.ParameterValues = new object[]
                    {
                        Element.Part<V_Not>(ParseExpression(skipIfContext[i].expr())),
                        new V_Number(Actions.Count - 1 - Actions.IndexOf(elseif_SkipIf) + (addIfElseSkip ? 1 : 0))
                    };

                    // Create the "Skip" dummy action.
                    if (addIfElseSkip)
                    {
                        elseif_Skips[i] = new A_Skip();
                        Actions.Add(elseif_Skips[i]);
                    }
                }

                // Parse else body.
                if (statementContext.@if().@else() != null)
                    ParseBlock(statementContext.@if().@else().block(), false);

                // Replace dummy skip with real skip now that we know the length of the if, if-else, and else's bodies.
                // Replace if's dummy.
                if_Skip.ParameterValues = new object[]
                {
                    new V_Number(Actions.Count - 1 - Actions.IndexOf(if_Skip))
                };

                // Replace else-if's dummy.
                for (int i = 0; i < elseif_Skips.Length; i++)
                {
                    elseif_Skips[i].ParameterValues = new object[]
                    {
                        new V_Number(Actions.Count - 1 - Actions.IndexOf(elseif_Skips[i]))
                    };
                }

                return;
            }

            #endregion

            #region return

            if (statementContext.RETURN() != null)
            {
                // Will have a value if the statement is "return value;", will be null if the statement is "return;".
                var returnExpr = statementContext.expr();


            }

            #endregion

            throw new Exception($"What's a {statementContext.GetChild(0)} ({statementContext.GetChild(0).GetType()})?");
        }

        Element ParseExpression(DeltinScriptParser.ExprContext context)
        {
            // If the expression is a(n)...

            #region Operation

            //   0       1      2
            // (expr operation expr)
            // count == 3
            if (context.ChildCount == 3
                &&(Constants.   MathOperations.Contains(context.GetChild(1).GetText())
                || Constants.CompareOperations.Contains(context.GetChild(1).GetText())
                || Constants.   BoolOperations.Contains(context.GetChild(1).GetText())))
            {
                Element left = ParseExpression(context.GetChild(0) as DeltinScriptParser.ExprContext);
                string operation = context.GetChild(1).GetText();
                Element right = ParseExpression(context.GetChild(2) as DeltinScriptParser.ExprContext);

                if (Constants.BoolOperations.Contains(context.GetChild(1).GetText()))
                {
                    if (left.ElementData.ValueType != Elements.ValueType.Any && left.ElementData.ValueType != Elements.ValueType.Boolean)
                        throw new SyntaxErrorException($"Expected boolean datatype, got {left .ElementData.ValueType.ToString()} instead.", context.start);
                    if (right.ElementData.ValueType != Elements.ValueType.Any && right.ElementData.ValueType != Elements.ValueType.Boolean)
                        throw new SyntaxErrorException($"Expected boolean datatype, got {right.ElementData.ValueType.ToString()} instead.", context.start);
                }

                switch (operation)
                {
                    case "^":
                        return Element.Part<V_RaiseToPower>(left, right);

                    case "*":
                        return Element.Part<V_Multiply>(left, right);

                    case "/":
                        return Element.Part<V_Divide>(left, right);

                    case "+":
                        return Element.Part<V_Add>(left, right);

                    case "-":
                        return Element.Part<V_Subtract>(left, right);

                    case "%":
                        return Element.Part<V_Modulo>(left, right);

                    // COMPARE : '<' | '<=' | '==' | '>=' | '>' | '!=';

                    case "&":
                        return Element.Part<V_And>(left, right);

                    case "|":
                        return Element.Part<V_Or>(left, right);

                    case "<":
                        return Element.Part<V_Compare>(left, Operators.LessThan, right);

                    case "<=":
                        return Element.Part<V_Compare>(left, Operators.LessThanOrEqual, right);

                    case "==":
                        return Element.Part<V_Compare>(left, Operators.Equal, right);

                    case ">=":
                        return Element.Part<V_Compare>(left, Operators.GreaterThanOrEqual, right);

                    case ">":
                        return Element.Part<V_Compare>(left, Operators.GreaterThan, right);

                    case "!=":
                        return Element.Part<V_Compare>(left, Operators.NotEqual, right);
                }
            }

            #endregion

            #region Not

            if (context.GetChild(0) is DeltinScriptParser.NotContext)
                return Element.Part<V_Not>(ParseExpression(context.GetChild(1) as DeltinScriptParser.ExprContext));

            #endregion

            #region Number

            if (context.GetChild(0) is DeltinScriptParser.NumberContext)
            {
                var number = context.GetChild(0);

                double num = double.Parse(number.GetChild(0).GetText());
                /*
                // num will have the format expr(number(X)) if positive, expr(number(neg(X))) if negative.
                if (number.GetChild(0) is DeltinScriptParser.NegContext)
                    // Is negative, use '-' before int.parse to make it negative.
                    num = -double.Parse(number.GetChild(0).GetText());
                else
                    // Is positive
                    num = double.Parse(number.GetChild(0).GetText());
                */

                return new V_Number(num);
            }

            #endregion

            #region Boolean

            // True
            if (context.GetChild(0) is DeltinScriptParser.TrueContext)
                return new V_True();

            // False
            if (context.GetChild(0) is DeltinScriptParser.FalseContext)
                return new V_False();

            #endregion

            #region String

            if (context.GetChild(0) is DeltinScriptParser.StringContext)
            {
                return V_String.ParseString(
                    context.start,
                    // String will look like "hey this is the contents", trim the quotes.
                    (context.GetChild(0) as DeltinScriptParser.StringContext).STRINGLITERAL().GetText().Trim('\"'),
                    null
                );
            }

            #endregion

            #region Formatted String

            if (context.GetChild(1) is DeltinScriptParser.StringContext)
            {
                Element[] values = context.expr().Select(expr => ParseExpression(expr)).ToArray();
                return V_String.ParseString(
                    context.start,
                    (context.GetChild(1) as DeltinScriptParser.StringContext).STRINGLITERAL().GetText().Trim('\"'),
                    values
                    );
            }

            #endregion

            #region null

            if (context.GetChild(0) is DeltinScriptParser.NullContext)
                return new V_Null();

            #endregion

            #region Group ( expr )

            if (context.ChildCount == 3 && context.GetChild(0).GetText() == "(" &&
                context.GetChild(1) is DeltinScriptParser.ExprContext &&
                context.GetChild(2).GetText() == ")")
                return ParseExpression(context.GetChild(1) as DeltinScriptParser.ExprContext);

            #endregion

            #region Method

            if (context.GetChild(0) is DeltinScriptParser.MethodContext)
                return ParseMethod(context.GetChild(0) as DeltinScriptParser.MethodContext, true);

            #endregion

            #region Variable

            if (context.GetChild(0) is DeltinScriptParser.VariableContext)
                return DefinedVar.GetVar((context.GetChild(0) as DeltinScriptParser.VariableContext).PART().GetText(), context.start).GetVariable(new V_EventPlayer());

            #endregion

            #region Array

            if (context.ChildCount == 4 && context.GetChild(1).GetText() == "[" && context.GetChild(3).GetText() == "]")
                return Element.Part<V_ValueInArray>(
                    ParseExpression(context.expr(0) as DeltinScriptParser.ExprContext),
                    ParseExpression(context.expr(1) as DeltinScriptParser.ExprContext));

            #endregion

            #region Create Array

            if (context.ChildCount >= 4 && context.GetChild(0).GetText() == "[")
            {
                var expressions = context.expr();
                V_Append prev = null;
                V_Append current = null;

                for (int i = 0; i < expressions.Length; i++)
                {
                    current = new V_Append()
                    {
                        ParameterValues = new object[2]
                    };

                    if (prev != null)
                        current.ParameterValues[0] = prev;
                    else
                        current.ParameterValues[0] = new V_EmptyArray();

                    current.ParameterValues[1] = ParseExpression(expressions[i]);
                    prev = current;
                }

                return current;
            }

            #endregion

            #region Empty Array

            if (context.ChildCount == 2 && context.GetText() == "[]")
                return Element.Part<V_EmptyArray>();

            #endregion

            #region Seperator/enum

            if (context.ChildCount == 3 && context.GetChild(1).GetText() == ".")
            {
                Element left = ParseExpression(context.GetChild(0) as DeltinScriptParser.ExprContext);
                string variableName = context.GetChild(2).GetChild(0).GetText();

                DefinedVar var = DefinedVar.GetVar(variableName, context.start);

                return var.GetVariable(left);
            }

            #endregion

            throw new Exception($"What's a {context.GetType().Name}?");
        }

        Element ParseMethod(DeltinScriptParser.MethodContext methodContext, bool needsToBeValue)
        {
            // Get the method name
            string methodName = methodContext.PART().GetText();

            // Get the kind of method the method is (Method (Overwatch), Custom Method, or User Method.)
            var methodType = GetMethodType(methodName);
            if (methodType == null)
                throw new SyntaxErrorException($"The method {methodName} does not exist.", methodContext.start);

            // Get the parameters
            var parameters = methodContext.expr();

            Element method;

            switch (methodType)
            {
                case MethodType.Method:
                {
                    Type owMethod = Element.GetMethod(methodName);

                    method = (Element)Activator.CreateInstance(owMethod);
                    Parameter[] parameterData = owMethod.GetCustomAttributes<Parameter>().ToArray();
                    object[] parsedParameters = new Element[parameterData.Length];

                    for (int i = 0; i < parameterData.Length; i++)
                    {
                        if (parameters.Length > i)
                            parsedParameters[i] = ParseParameter(parameters[i], methodName, parameterData[i]);
                        else 
                        {
                            if (parameterData[i].DefaultType == null)
                                throw new SyntaxErrorException($"Missing parameter {parameterData[i].Name} in the method {methodName} and no default type to fallback on.", 
                                    parameters[i].start);
                            else
                                parsedParameters[i] = parameterData[i].GetDefault();
                        }
                    }

                    method.ParameterValues = parsedParameters;
                    break;
                }

                case MethodType.CustomMethod:
                {
                    MethodInfo customMethod = CustomMethods.GetCustomMethod(methodName);
                    Parameter[] parameterData = customMethod.GetCustomAttributes<Parameter>().ToArray();
                    object[] parsedParameters = new Element[parameterData.Length];

                    for (int i = 0; i < parameterData.Length; i++)
                        if (parameters.Length > i)
                            parsedParameters[i] = ParseParameter(parameters[i], methodName, parameterData[i]);
                        else
                            throw new SyntaxErrorException($"Missing parameter {parameterData[i].Name} in the method {methodName} and no default type to fallback on.", 
                                parameters[i].start);

                    MethodResult result = (MethodResult)customMethod.Invoke(null, new object[] { IsGlobal, parsedParameters });
                    switch (result.MethodType)
                    {
                        case CustomMethodType.Action:
                            if (needsToBeValue)
                                throw new IncorrectElementTypeException(methodName, true);
                            break;

                        case CustomMethodType.MultiAction_Value:
                        case CustomMethodType.Value:
                            if (!needsToBeValue)
                                throw new IncorrectElementTypeException(methodName, false);
                            break;
                    }

                    // Some custom methods have extra actions.
                    if (result.Elements != null)
                        Actions.AddRange(result.Elements);
                    method = result.Result;

                    break;
                }

                case MethodType.UserMethod:
                {
                    UserMethod userMethod = UserMethod.GetUserMethod(methodName);

                    method = ParseBlock(userMethod.Block, true);

                    break;
                }

                default: throw new NotImplementedException();
            }

            return method;
        }

        object ParseParameter(DeltinScriptParser.ExprContext context, string methodName, Parameter parameterData)
        {
            object value = null;

            if (context.GetChild(0) is DeltinScriptParser.EnumContext)
            {
                if (parameterData.ParameterType != ParameterType.Enum)
                    throw new SyntaxErrorException($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\"."
                        , context.start);

                string type = context.GetText().Split('.').ElementAtOrDefault(0);
                string enumValue = context.GetText().Split('.').ElementAtOrDefault(1);

                if (type != parameterData.EnumType.Name)
                    throw new SyntaxErrorException($"Expected enum type \"{parameterData.EnumType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\"."
                        , context.start);

                try
                {
                    value = Enum.Parse(parameterData.EnumType, enumValue);
                }
                catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is OverflowException)
                {
                    throw new SyntaxErrorException($"The value {enumValue} does not exist in the enum {type}.");
                }

                if (value == null)
                    throw new SyntaxErrorException($"Could not parse enum parameter {context.GetText()}."
                        , context.start);
            }

            else
            {
                if (parameterData.ParameterType != ParameterType.Value)
                    throw new SyntaxErrorException($"Expected enum type \"{parameterData.EnumType.Name}\" on {methodName}'s parameter \"{parameterData.Name}\"."
                        , context.start);

                value = ParseExpression(context);

                Element element = value as Element;
                ElementData elementData = element.GetType().GetCustomAttribute<ElementData>();

                if (elementData.ValueType != Elements.ValueType.Any &&
                    !parameterData.ValueType.HasFlag(elementData.ValueType))
                    throw new SyntaxErrorException($"Expected value type \"{parameterData.ValueType.ToString()}\" on {methodName}'s parameter \"{parameterData.Name}\", got \"{elementData.ValueType.ToString()}\" instead."
                        , context.start);
            }

            if (value == null)
                throw new SyntaxErrorException("Could not parse parameter.", context.start);


            return value;
        }

        private static MethodType? GetMethodType(string name)
        {
            if (Element.GetMethod(name) != null)
                return MethodType.Method;
            if (CustomMethods.GetCustomMethod(name) != null)
                return MethodType.CustomMethod;
            if (UserMethod.GetUserMethod(name) != null)
                return MethodType.UserMethod;
            return null;
        }

        enum MethodType
        {
            Method,
            CustomMethod,
            UserMethod
        }
    }

    class Var
    {
        public static Variable Global { get; private set; }
        public static Variable Player { get; private set; }

        private static int NextFreeGlobalIndex { get; set; }
        private static int NextFreePlayerIndex { get; set; }

        public static void Setup(Variable global, Variable player)
        {
            Global = global;
            Player = player;
        }

        public static int Assign(bool isGlobal)
        {
            if (isGlobal)
            {
                int index = NextFreeGlobalIndex;
                NextFreeGlobalIndex++;
                return index;
            }
            else
            {
                int index = NextFreePlayerIndex;
                NextFreePlayerIndex++;
                return index;
            }
        }

        private static Variable GetVar(bool isGlobal)
        {
            if (isGlobal)
                return Global;
            else
                return Player;
        }

        public static Var AssignVar(bool isGlobal)
        {
            return new Var(isGlobal, GetVar(isGlobal), Assign(isGlobal));
        }

        public static DefinedVar AssignDefinedVar(bool isGlobal, string name, IToken token)
        {
            return new DefinedVar(name, isGlobal, GetVar(isGlobal), Assign(isGlobal), token);
        }



        public bool IsGlobal { get; protected set; }
        public Variable Variable { get; protected set; }

        public bool IsInArray { get; protected set; }
        public int Index { get; protected set; }

        public Var(bool isGlobal, Variable variable, int index)
        {
            IsGlobal = isGlobal;
            Variable = variable;
            Index = index;
            IsInArray = index != -1;
        }

        protected Var()
        {}

        public Element GetVariable(Element targetPlayer = null, Element getAiIndex = null)
        {
            Element element;

            if (targetPlayer == null)
                targetPlayer = new V_EventPlayer();

            if (getAiIndex == null)
            {
                if (IsInArray)
                {
                    if (IsGlobal)
                        element = Element.Part<V_ValueInArray>(Element.Part<V_GlobalVariable>(Variable), new V_Number(Index));
                    else
                        element = Element.Part<V_ValueInArray>(Element.Part<V_PlayerVariable>(targetPlayer, Variable), new V_Number(Index));
                }
                else
                {
                    if (IsGlobal)
                        element = Element.Part<V_GlobalVariable>(Variable);
                    else
                        element = Element.Part<V_PlayerVariable>(targetPlayer, Variable);
                }
            }
            else
            {
                if (IsInArray)
                {
                    if (IsGlobal)
                        element = Element.Part<V_ValueInArray>(Element.Part<V_ValueInArray>(Element.Part<V_GlobalVariable>(Variable)), new V_Number(Index));
                    else
                        element = Element.Part<V_ValueInArray>(Element.Part<V_ValueInArray>(Element.Part<V_PlayerVariable>(targetPlayer, Variable)), new V_Number(Index));
                }
                else
                {
                    if (IsGlobal)
                        element = Element.Part<V_ValueInArray>(Element.Part<V_GlobalVariable>(Variable), getAiIndex);
                    else
                        element = Element.Part<V_ValueInArray>(Element.Part<V_PlayerVariable>(targetPlayer, Variable), getAiIndex);
                }
            }

            return element;
        }

        public Element SetVariable(Element value, Element targetPlayer = null, Element setAtIndex = null)
        {
            Element element;

            if (targetPlayer == null)
                targetPlayer = new V_EventPlayer();

            if (setAtIndex == null)
            {
                if (IsInArray)
                {
                    if (IsGlobal)
                        element = Element.Part<A_SetGlobalVariableAtIndex>(Variable, new V_Number(Index), value);
                    else
                        element = Element.Part<A_SetPlayerVariableAtIndex>(targetPlayer, Variable, new V_Number(Index), value);
                }
                else
                {
                    if (IsGlobal)
                        element = Element.Part<A_SetGlobalVariable>(Variable, value);
                    else
                        element = Element.Part<A_SetPlayerVariable>(targetPlayer, Variable, value);
                }
            }
            else
            {
                if (IsInArray)
                {
                    if (IsGlobal)
                        element = Element.Part<A_SetGlobalVariableAtIndex>(Variable, new V_Number(Index), 
                            Element.Part<V_Append>(
                                Element.Part<V_Append>(
                                    Element.Part<V_ArraySlice>(GetVariable(targetPlayer), new V_Number(0), setAtIndex), 
                                    value),
                            Element.Part<V_ArraySlice>(GetVariable(targetPlayer), Element.Part<V_Add>(setAtIndex, new V_Number(1)), new V_Number(9999))));
                    else
                        element = Element.Part<A_SetPlayerVariableAtIndex>(targetPlayer, Variable, new V_Number(Index),
                            Element.Part<V_Append>(
                                Element.Part<V_Append>(
                                    Element.Part<V_ArraySlice>(GetVariable(targetPlayer), new V_Number(0), setAtIndex),
                                    value),
                            Element.Part<V_ArraySlice>(GetVariable(targetPlayer), Element.Part<V_Add>(setAtIndex, new V_Number(1)), new V_Number(9999))));
                }
                else
                {
                    if (IsGlobal)
                        element = Element.Part<A_SetGlobalVariableAtIndex>(Variable, setAtIndex, value);
                    else
                        element = Element.Part<A_SetPlayerVariableAtIndex>(targetPlayer, Variable, setAtIndex, value);
                }
            }

            return element;

        }
    }

    class DefinedVar : Var
    {
        public string Name { get; protected set; }

        public static List<DefinedVar> VarCollection { get; private set; } = new List<DefinedVar>();

        private static bool IsVar(string name)
        {
            return VarCollection.Any(v => v.Name == name);
        }

        public static DefinedVar GetVar(string name, IToken token)
        {
            DefinedVar var = VarCollection.FirstOrDefault(v => v.Name == name);

            if (var == null)
                throw new SyntaxErrorException($"The variable {name} does not exist.", token);

            return var;
        }

        public DefinedVar(DeltinScriptParser.VardefineContext vardefine)
        {
            IsGlobal = vardefine.GLOBAL() != null;
            string name = vardefine.PART(0).GetText();

            if (IsVar(name))
                throw new SyntaxErrorException($"The variable {name} was already defined.", vardefine.start);

            Name = name;

            // Both can be null, or only one can have a value.
            string useVar = vardefine.PART(1)?.GetText();
            var useNumber = vardefine.number();

            // Auto assign
            if (useNumber == null && useVar == null)
            {
                Index = Var.Assign(IsGlobal);

                if (IsGlobal)
                    Variable = Var.Global;
                else
                    Variable = Var.Player;

                IsInArray = true;
            }
            else
            {
                if (useNumber != null)
                {
                    IsInArray = true;
                    string indexString = useNumber.GetText();
                    if (!int.TryParse(indexString, out int index))
                        throw new SyntaxErrorException("Expected number.", useNumber.start);
                    Index = index;
                }

                if (useVar != null)
                {
                    if (!Enum.TryParse(useVar, out Variable var))
                        throw new SyntaxErrorException("Expected variable.", vardefine.start);
                    Variable = var;
                }
            }

            VarCollection.Add(this);
        }

        public DefinedVar(string name, bool isGlobal, Variable variable, int index, IToken token)
        {
            if (IsVar(name))
                throw new SyntaxErrorException($"The variable {name} was already defined.", token);

            Name = name;
            IsGlobal = isGlobal;
            Variable = variable;

            if (index != -1)
            {
                IsInArray = true;
                Index = index;
            }

            VarCollection.Add(this);
        }

        public void OutOfScope()
        {
            VarCollection.Remove(this);
        }
    }

    class UserMethod 
    {
        public UserMethod(DeltinScriptParser.User_methodContext context)
        {
            Name = context.PART().GetText();
            Block = context.block();

            var returnType = context.DATA_TYPE();

            if (context.VOID() != null)
                ReturnType = null;
            else
                ReturnType = (Elements.ValueType?)Enum.Parse(typeof(Elements.ValueType), returnType.ToString());

            /*
            var contextParams = context.user_method_parameter();
            Parameters = new UserMethodParameter[contextParams.Length];
            for (int i = 0; i < Parameters.Length; i++)
                Parameters[i] = new UserMethodParameter(contextParams[i]);
            */

            var contextParams = context.user_method_parameter();
            Parameters = new Parameter[contextParams.Length];

            for (int i = 0; i < Parameters.Length; i++)
            {
                var name = contextParams[i].PART().GetText();
                var type = (Elements.ValueType)Enum.Parse(typeof(Elements.ValueType), contextParams[i].DATA_TYPE().GetText());

                Parameters[i] = new Parameter(name, type, null);
            }

            UserMethodCollection.Add(this);
        }

        public string Name { get; private set; }

        public Elements.ValueType? ReturnType { get; private set; }
        public DeltinScriptParser.BlockContext Block { get; private set; }

        /*
        public UserMethodParameter[] Parameters { get; private set; }
        */
        public Parameter[] Parameters { get; private set; }

        public static readonly List<UserMethod> UserMethodCollection = new List<UserMethod>();

        public static UserMethod GetUserMethod(string name)
        {
            return UserMethodCollection.FirstOrDefault(um => um.Name == name);
        }
    }

    /*
    class UserMethodParameter
    {
        public UserMethodParameter(DeltinScriptParser.User_method_parameterContext context)
        {
            Name = context.PART().GetText();
            Type = (Elements.ValueType)Enum.Parse(typeof(Elements.ValueType), context.DATA_TYPE().GetText());
        }

        public UserMethodParameter(string name, Elements.ValueType type)
        {
            Name = name;
            Type = type;
        }
        public string Name { get; private set; }
        public Elements.ValueType Type { get; private set; }
    }
    */

    public class ErrorListener : BaseErrorListener
    {
        public override void SyntaxError(IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            throw new SyntaxErrorException(msg, offendingSymbol);
        }
    }

    class Visitor : DeltinScriptBaseVisitor<object>
    {
    }
}
