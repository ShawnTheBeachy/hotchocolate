﻿using System;
using System.Collections.Generic;
using System.Linq;
using HotChocolate;
using HotChocolate.Language;
using HotChocolate.Types;
using StrawberryShake.CodeGeneration.Analyzers;
using StrawberryShake.CodeGeneration.Analyzers.Models;
using StrawberryShake.CodeGeneration.Utilities;
using WithDirectives = HotChocolate.Language.IHasDirectives;
using static StrawberryShake.CodeGeneration.Utilities.NameUtils;

namespace StrawberryShake.Generators
{
    internal abstract class SelectionSetModelGenerator<T>
        where T : INamedType
    {
        protected ComplexOutputTypeModel CreateTypeModel(
            IModelGeneratorContext context,
            IFragmentNode fragmentNode,
            Path path)
        {
            var levels = new Stack<ISet<string>>();
            levels.Push(new HashSet<string>());
            return CreateTypeModel(context, fragmentNode, path, levels);
        }

        private ComplexOutputTypeModel CreateTypeModel(
            IModelGeneratorContext context,
            IFragmentNode fragmentNode,
            Path path,
            Stack<ISet<string>> levels)
        {
            ISet<string> implementedFields = levels.Peek();
            IReadOnlyList<OutputFieldModel> fieldModels = Array.Empty<OutputFieldModel>();

            IReadOnlyList<ComplexOutputTypeModel> implements =
                CreateChildTypeModles(
                    context,
                    fragmentNode,
                    path,
                    levels,
                    implementedFields);

            if (fragmentNode.Fragment.TypeCondition is IComplexOutputType type)
            {
                fieldModels = CreateFields(
                    type,
                    fragmentNode.Fragment.SelectionSet.Selections,
                    name =>
                    {
                        if (implementedFields.Add(name))
                        {
                            return true;
                        }
                        return false;
                    },
                    path);
            }

            NameString name = context.GetOrCreateName(
                fragmentNode.Fragment.SelectionSet,
                GetClassName(fragmentNode.Name));

            var typeModel = new ComplexOutputTypeModel(
                name,
                fragmentNode.Fragment.TypeCondition.Description,
                fragmentNode.Fragment.TypeCondition,
                fragmentNode.Fragment.SelectionSet,
                implements,
                fieldModels);

            context.RegisterType(typeModel);

            return typeModel;
        }

        private IReadOnlyList<ComplexOutputTypeModel> CreateChildTypeModles(
            IModelGeneratorContext context,
            IFragmentNode fragmentNode,
            Path path,
            Stack<ISet<string>> levels,
            ISet<string> implementedFields)
        {
            var implementedByChildren = new HashSet<string>();
            levels.Push(implementedByChildren);

            var implements = new List<ComplexOutputTypeModel>();

            foreach (IFragmentNode child in fragmentNode.Children)
            {
                implements.Add(CreateTypeModel(context, child, path, levels));
            }

            levels.Pop();

            foreach (string fieldName in implementedByChildren)
            {
                implementedFields.Add(fieldName);
            }

            return implements;
        }

        private IReadOnlyList<OutputFieldModel> CreateFields(
            IComplexOutputType type,
            IEnumerable<ISelectionNode> selections,
            Func<string, bool> addField,
            Path path)
        {
            var fields = new Dictionary<string, FieldSelection>();

            foreach (FieldNode selection in selections.OfType<FieldNode>())
            {
                NameString responseName = selection.Alias == null
                    ? selection.Name.Value
                    : selection.Alias.Value;

                if (addField(responseName))
                {
                    FieldCollector.ResolveFieldSelection(
                        type,
                        selection,
                        path,
                        fields);
                }
            }

            return fields.Values.Select(t =>
            {
                string responseName = (t.Selection.Alias ?? t.Selection.Name).Value;
                return new OutputFieldModel(
                    GetPropertyName(t.ResponseName),
                    t.Field.Description,
                    t.Field,
                    t.Field.Type,
                    t.Selection,
                    path.Append(responseName));
            }).ToList();
        }

        protected ComplexOutputTypeModel CreateClassModel(
            IModelGeneratorContext context,
            IFragmentNode fragmentNode,
            ComplexOutputTypeModel returnType,
            SelectionInfo selection,
            ICollection<ComplexOutputTypeModel> possibleTypes)
        {
            var fieldNames = new HashSet<string>(
                selection.Fields.Select(t => GetPropertyName(t.ResponseName)));

            string className = context.GetOrCreateName(
                fragmentNode.Fragment.SelectionSet,
                GetClassName(returnType.Name),
                fieldNames);

            var modelClass = new ComplexOutputTypeModel(
                className,
                fragmentNode.Fragment.TypeCondition.Description,
                fragmentNode.Fragment.TypeCondition,
                fragmentNode.Fragment.SelectionSet,
                new[] { returnType },
                Array.Empty<OutputFieldModel>());

            context.RegisterType(modelClass);

            possibleTypes.Add(modelClass);
        }

        protected void CreateClassTypeModels(
            IModelGeneratorContext context,
            FieldNode fieldSelection,
            IFragmentNode returnType,
            ComplexOutputTypeModel interfaceDescriptor,
            IReadOnlyCollection<SelectionInfo> selections,
            List<ResultParserTypeDescriptor> resultParserTypes,
            Path path)
        {
            foreach (SelectionInfo selection in selections)
            {
                IFragmentNode modelType = ResolveReturnType(
                    context,
                    selection.Type,
                    fieldSelection,
                    selection);

                var interfaces = new List<IInterfaceDescriptor>();

                foreach (IFragmentNode fragment in ShedNonMatchingFragments(selection.Type, modelType))
                {
                    interfaces.Add(CreateInterfaceModel(context, fragment, path));
                }

                interfaces.Insert(0, interfaceDescriptor);

                NameString typeName = HoistName(selection.Type, modelType);
                if (typeName.IsEmpty)
                {
                    typeName = selection.Type.Name;
                }

                bool update = false;

                var fieldNames = new HashSet<string>(
                    selection.Fields.Select(t => GetPropertyName(t.ResponseName)));

                string className = context.GetOrCreateName(
                    modelType.Fragment.SelectionSet,
                    GetClassName(typeName),
                    fieldNames);

                if (context.TryGetDescriptor(className, out ClassDescriptor? modelClass))
                {
                    var interfaceNames = new HashSet<string>(interfaces.Select(t => t.Name));
                    foreach (IInterfaceDescriptor item in modelClass!.Implements.Reverse())
                    {
                        if (!interfaceNames.Contains(item.Name))
                        {
                            interfaces.Insert(0, item);
                        }
                    }
                    update = true;
                }

                modelClass = new ClassDescriptor(
                    className,
                    context.Namespace,
                    selection.Type,
                    interfaces);

                context.Register(modelClass, update);
                resultParserTypes.Add(new ResultParserTypeDescriptor(modelClass));
            }
        }

        protected IFragmentNode HoistFragment(
            INamedType type,
            IFragmentNode fragmentNode)
        {
            (SelectionSetNode s, IReadOnlyList<IFragmentNode> f) current =
                (fragmentNode.Fragment.SelectionSet, fragmentNode.Children);
            IFragmentNode selected = fragmentNode;

            while (!current.s.Selections.OfType<FieldNode>().Any()
                && current.f.Count == 1
                && TypeHelpers.DoesTypeApply(current.f[0].Fragment.TypeCondition, type))
            {
                selected = current.f[0];
                current = (selected.Fragment.SelectionSet, selected.Children);
            }

            return selected;
        }

        protected NameString HoistName(
            INamedType type,
            IFragmentNode fragmentNode)
        {
            if (fragmentNode.Fragment.TypeCondition.Name.Equals(type.Name))
            {
                return fragmentNode.Name;
            }
            else
            {
                foreach (IFragmentNode child in fragmentNode.Children)
                {
                    NameString name = HoistName(type, child);
                    if (name.HasValue)
                    {
                        return name;
                    }
                }

                return default;
            }
        }

        private static bool FieldSelectionsAreEqual(
            IReadOnlyList<FieldSelection> a,
            IReadOnlyList<FieldSelection> b)
        {
            if (a.Count == b.Count)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (!ReferenceEquals(a[i].Field, b[i].Field))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        protected static string CreateName(
            IType returnType,
            WithDirectives withDirectives,
            Func<string, string> nameFormatter)
        {
            if (TryGetTypeName(withDirectives, out string? typeName))
            {
                return nameFormatter(typeName!);
            }
            else if (withDirectives is OperationDefinitionNode operation)
            {
                return nameFormatter(operation.Name!.Value);
            }

            INamedType type = returnType.NamedType();

            if (type is HotChocolate.Types.IHasDirectives d)
            {
                IDirective directive =
                    d.Directives[GeneratorDirectives.Name].FirstOrDefault();
                if (directive is { })
                {
                    return nameFormatter(directive.ToObject<NameDirective>().Value);
                }
            }

            return nameFormatter(returnType.NamedType().Name);
        }

        protected static bool TryGetTypeName(
            WithDirectives withDirectives,
            out string? typeName)
        {
            DirectiveNode directive =
                withDirectives.Directives.FirstOrDefault(t =>
                    t.Name.Value.EqualsOrdinal(GeneratorDirectives.Type));

            if (directive is null)
            {
                typeName = null;
                return false;
            }

            typeName = directive.Arguments.Single(a =>
                a.Name.Value.EqualsOrdinal("name")).Value.Value as string;
            return true;
        }

        protected IReadOnlyList<IFragmentNode> ShedNonMatchingFragments(
            INamedType namedType,
            IFragmentNode fragmentNode)
        {
            var nodes = new List<IFragmentNode>();

            if (fragmentNode.Fragment.TypeCondition.Name.Equals(namedType.Name))
            {
                ShedNonMatchingFragments(namedType, fragmentNode, nodes.Add);
            }
            else
            {
                foreach (IFragmentNode child in fragmentNode.Children)
                {
                    ShedNonMatchingFragments(namedType, child, nodes.Add);
                }
            }

            return nodes;
        }

        private void ShedNonMatchingFragments(
            INamedType namedType,
            IFragmentNode fragmentNode,
            Action<IFragmentNode> add)
        {
            if (fragmentNode.Fragment.TypeCondition.Name.Equals(namedType.Name))
            {
                add(fragmentNode);
            }
            else
            {
                foreach (IFragmentNode child in fragmentNode.Children)
                {
                    ShedNonMatchingFragments(namedType, child, add);
                }
            }
        }

        protected IFragmentNode ResolveReturnType(
            IModelGeneratorContext context,
            INamedType namedType,
            FieldNode fieldSelection,
            SelectionInfo selection)
        {
            var returnType = new FragmentNode(new Fragment(
                CreateName(namedType, fieldSelection, GetClassName),
                namedType,
                selection.SelectionSet));

            returnType.Children.AddRange(selection.Fragments);

            return HoistFragment(namedType, returnType);
        }
    }
}
