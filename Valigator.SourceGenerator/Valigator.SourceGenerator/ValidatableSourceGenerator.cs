using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Valigator.SourceGenerator.Builders;
using Valigator.SourceGenerator.Dtos;
using Valigator.SourceGenerator.Utils;
using Valigator.SourceGenerator.Utils.Mapping;
using Valigator.SourceGenerator.Utils.SourceTexts;
using Valigator.SourceGenerator.Utils.SourceTexts.FileBuilders;
using Valigator.SourceGenerator.ValueProviders;

namespace Valigator.SourceGenerator;

[Generator]
public class ValidatableSourceGenerator : IIncrementalGenerator
{
	public void Initialize(IncrementalGeneratorInitializationContext initContext)
	{
		var allValidators = ValidatorsIncrementalValueProvider.Get(initContext);
		var validatableObjects = ValidatableObjectIncrementalValueProvider.Get(initContext);
		var config = ConfigOptionsProvider.Get(initContext);

		initContext.RegisterSourceOutput(
			validatableObjects
				.Combine(config)
				.Combine(allValidators)
				.Select((tuple, _) => (tuple.Left.Left, tuple.Left.Right, tuple.Right)),
			ExecuteValidatorGeneration
		);
	}

	private static void ExecuteValidatorGeneration(
		SourceProductionContext context,
		(
			ObjectProperties Object,
			ValigatorConfiguration Config,
			EquatableArray<ValidatorProperties> Validators
		) properties
	)
	{
		var methods = properties.Object.Methods.ToDictionary(x => x.MethodName, x => x);

		// Name of the class with RULEs
		string rulesClassName = $"{properties.Object.Name}Rules";
		string customValidatorInterfaceName = $"I{properties.Object.Name}CustomValidation";

		var dependencies = new DependenciesTracker();
		AppendDependenciesOfBeforeAndAfterMethods(properties.Object, dependencies);

		// Builders
		var rulesClassBuilder = new RulesClassBuilder(rulesClassName);
		var customValidationsInterfaceBuilder = new CustomValidationInterfaceBuilder(
			customValidatorInterfaceName,
			dependencies
		).WithMethods(methods);
		var invocationBuilder = new PropertiesValidationInvocationBuilder(
			rulesClassName,
			properties.Config,
			dependencies
		);

		// Generate stuff for each property
		foreach (PropertyProperties property in properties.Object.Properties)
		{
			var attributes = ToAttributeList(properties, property);

			// Skip properties without validators
			if (attributes.Count == 0)
			{
				continue;
			}

			ProcessValidatableProperty(
				property,
				properties.Validators,
				attributes,
				rulesClassBuilder,
				customValidationsInterfaceBuilder,
				invocationBuilder
			);
		}

		bool isAsync =
			customValidationsInterfaceBuilder.Calls.AnyAsync()
			|| invocationBuilder.Calls.AnyAsync()
			|| (
				(properties.Object.BeforeValidateMethod?.ReturnTypeType ?? ReturnTypeType.None)
				& ReturnTypeType.Awaitable
			) != 0
			|| (
				(properties.Object.AfterValidateMethod?.ReturnTypeType ?? ReturnTypeType.None)
				& ReturnTypeType.Awaitable
			) != 0
			// TODO: Can we handle this differently? How should we check if that type is async validator? For now, we force async behavior if there is any property of Validatable type
			|| properties.Object.Properties.Any(prop => prop.PropertyIsOfValidatableType)
			// TODO: Like above, we force async behavior if the object inherits from Validatable object
			|| properties.Object.InheritsValidatableObject;

		// Generate the validator part for the original object
		// > public partial class Xxx : IValidatable, IInternalValidationInvoker { ... }
		var validatorClassBuilder = DeclarationBuilder
			.CreateClassOrRecord(properties.Object.ClassOrRecordKeyword, properties.Object.Name)
			.SetAccessModifier(properties.Object.Accessibility)
			.AddUsings(properties.Object.Usings.GetArray() ?? Array.Empty<string>())
			// .SetNamespace(properties.Object.Namespace)
			.Partial()
			.AddInterfaces(Consts.IValidatableGlobalRef)
			.AddInterfaces(Consts.InternalValidationInvokerGlobalRef)
			.AddMember(
				CreateValidateMethod(
					properties,
					dependencies,
					customValidationsInterfaceBuilder.HasAnyMethod(),
					customValidatorInterfaceName,
					invocationBuilder,
					isAsync
				)
			);

		// If custom validation is used, add generated interface to the validator class
		if (customValidationsInterfaceBuilder.HasAnyMethod())
		{
			// Add the interface on validator part of the original object
			validatorClassBuilder.AddInterfaces(customValidatorInterfaceName);
		}

		var sourceText =
			"// <auto-generated/>"
			+ Environment.NewLine
			+ "#nullable enable"
			+ Environment.NewLine
			+ Environment.NewLine
			+ $"namespace {properties.Object.Namespace}{Environment.NewLine}{{{Environment.NewLine}"
			+ validatorClassBuilder.Build().Indent()
			+ Environment.NewLine
			+ rulesClassBuilder.Build().Indent()
			+ Environment.NewLine
			+ customValidationsInterfaceBuilder.Build().Indent()
			+ Environment.NewLine
			+ "}";

		context.AddSource($"{properties.Object.Name}.Validator.g.cs", SourceText.From(sourceText, Encoding.UTF8));
	}

	private static void AppendDependenciesOfBeforeAndAfterMethods(
		ObjectProperties objectProperties,
		DependenciesTracker dependencies
	)
	{
		if (objectProperties.BeforeValidateMethod is not null)
		{
			foreach (string dependency in objectProperties.BeforeValidateMethod.Dependencies)
			{
				dependencies.AddDependency(dependency);
			}
		}

		if (objectProperties.AfterValidateMethod is not null)
		{
			foreach (string dependency in objectProperties.AfterValidateMethod.Dependencies)
			{
				dependencies.AddDependency(dependency);
			}
		}
	}

	/// <summary>
	/// Create list of validation attributes. Include auto validators if enabled.
	/// </summary>
	/// <param name="properties"></param>
	/// <param name="property"></param>
	/// <returns></returns>
	private static List<AttributeProperties> ToAttributeList(
		(
			ObjectProperties Object,
			ValigatorConfiguration Config,
			EquatableArray<ValidatorProperties> Validators
		) properties,
		PropertyProperties property
	)
	{
		var attributes = property.Attributes.GetArray()?.ToList() ?? new List<AttributeProperties>();

		// Add automatic validators
		AddAutoValidators(attributes, property, properties.Config, properties.Object.UseAutoValidators);

		return attributes;
	}

	private static SourceTextSectionBuilder CreateValidateMethod(
		(
			ObjectProperties Object,
			ValigatorConfiguration Config,
			EquatableArray<ValidatorProperties> Validators
		) properties,
		DependenciesTracker dependencies,
		bool hasCustomValidation,
		string customValidatorInterfaceName,
		PropertiesValidationInvocationBuilder invocationBuilder,
		bool isAsync
	)
	{
		var asyncKeyword = isAsync ? "async " : string.Empty;

		var validateMethodFilePart = new SourceTextSectionBuilder()
			.AppendLine("/// <inheritdoc />")
			.Append($"{asyncKeyword}ValueTask<{Consts.ValidationResultGlobalRef}>")
			.AppendLine($" {Consts.InternalValidationInvokerGlobalRef}.Validate(")
			.AppendLine($"\t{Consts.ValidationContextGlobalRef} context,")
			.AppendLine($"\t{Consts.ServiceProviderGlobalRef}? serviceProvider")
			.AppendLine(")")
			.AppendLine("{");

		if (hasCustomValidation)
		{
			validateMethodFilePart
				.AppendLine($"\tvar customValidator = ({customValidatorInterfaceName})this;")
				.AppendLine();
		}

		var nestedValidations = new List<string>();
		var nestedValidationsDispose = new List<string>();

		foreach (var property in properties.Object.Properties)
		{
			if (!property.PropertyIsOfValidatableType)
			{
				continue;
			}

			nestedValidations.Add(
				$"""
				context.SetObject(this.{property.PropertyName});
				var nestedValidationResult{nestedValidationsDispose.Count} = await (({Consts.InternalValidationInvokerGlobalRef})this.{property.PropertyName}).Validate(context, serviceProvider);
				nestedPropertiesCount += (({Consts.InternalValidationResultGlobalRef})nestedValidationResult{nestedValidationsDispose.Count}).GetPropertiesCount();
				"""
			);

			nestedValidationsDispose.Add(
				$"""
				result.CombineNested(nestedValidationResult{nestedValidationsDispose.Count}, "{property.PropertyName}");
				nestedValidationResult{nestedValidationsDispose.Count}.Dispose();
				"""
			);
		}

		var hasNestedProperties = nestedValidations.Count != 0 || properties.Object.InheritsValidatableObject;

		if (hasNestedProperties)
		{
			validateMethodFilePart.AppendLine("\tvar nestedPropertiesCount = 0;");

			if (nestedValidations.Count != 0)
			{
				validateMethodFilePart.AppendLine(
					$"""

					{string.Join(Environment.NewLine, nestedValidations)}
					context.SetObject(this);

					""".Indent(2)
				);
			}

			if (properties.Object.InheritsValidatableObject)
			{
				validateMethodFilePart.AppendLine(
					$"""
					// call BASE class' Validate()
					var baseResult = await (({Consts.InternalValidationInvokerGlobalRef})base).Validate(validationContext, serviceProvider);
					nestedPropertiesCount += (({Consts.InternalValidationResultGlobalRef})baseResult).GetPropertiesCount();
					""".Indent()
				);
			}
		}

		// string globalMessages = "[]";
		validateMethodFilePart.AppendLine(
			$"\tvar result = {Consts.ExtendableValidationResultGlobalRef}.Create({properties.Object.Properties.Count}{(hasNestedProperties ? " + nestedPropertiesCount" : string.Empty)});"
		);

		if (nestedValidations.Count != 0)
		{
			validateMethodFilePart.AppendLine(
				$"""

				// Dispose nested validation results
				{string.Join(Environment.NewLine, nestedValidationsDispose)}
				""".Indent(2)
			);
		}

		if (properties.Object.InheritsValidatableObject)
		{
			validateMethodFilePart.AppendLine(
				"""
				result.Combine(baseResult);
				baseResult.Dispose();
				""".Indent(2)
			);
			// validateMethodFilePart
			// 	.AppendLine("\t// call BASE class' Validate()")
			// 	.AppendLine(
			// 		$"\tvar baseResult = await (({Consts.InternalValidationInvokerGlobalRef})base).Validate(validationContext, serviceProvider);"
			// 	)
			// 	.AppendLine("\tresult.AddGlobalMessages(baseResult.Global);")
			// 	.AppendLine()
			// 	.AppendLine("\tforeach (var baseProp in baseResult.Properties) {")
			// 	.AppendLine("\t\tresult.AddPropertyResult(baseProp);")
			// 	.AppendLine("\t}")
			// 	.AppendLine();
		}

		if (dependencies.HasDependencies)
		{
			validateMethodFilePart
				.AppendLine("\t// Required services")
				.AppendLine("\tvar serviceValidationContext = context;")
				.AppendLine("\tvar serviceExtendableValidationResult = result;")
				.AppendLine("\tvar serviceValidationResult = result;")
				.AppendLine(
					string.Join(
						Environment.NewLine,
						dependencies.Services.Select(dependency =>
							$"\tvar service{dependency} = serviceProvider!.GetRequiredService<{dependency}>();"
						)
					)
				)
				.AppendLine();
		}

		// BeforeValidate hook
		AppendBeforeValidateHook(properties.Object, validateMethodFilePart, isAsync);

		// Add INVOCATIONS
		validateMethodFilePart.AppendLine(invocationBuilder.Build().Indent(2));

		// if (properties.Object.InheritsValidatableObject)
		// {
		// 	validateMethodFilePart
		// 		.AppendLine("\tforeach (var baseProp in baseResult.Properties) {")
		// 		.AppendLine("\t\tresult.AddPropertyResult(baseProp);")
		// 		.AppendLine("\t}")
		// 		.AppendLine();
		// }

		// AfterValidate hook
		var afterValidateMethodReturns = AppendAfterValidateHook(properties.Object, validateMethodFilePart);

		if (!afterValidateMethodReturns)
		{
			validateMethodFilePart.AppendLine(
				isAsync ? "\treturn result;" : $"\treturn new ValueTask<{Consts.ValidationResultGlobalRef}>(result);"
			);
		}

		validateMethodFilePart.AppendLine("}");

		// Explicit implementation of IValidatable
		validateMethodFilePart
			.AppendLine()
			.AppendLine("/// <inheritdoc />")
			.Append($"async ValueTask<{Consts.ValidationResultGlobalRef}>")
			.Append($" {Consts.IValidatableGlobalRef}.Validate(IServiceProvider serviceProvider)")
			.AppendLine("{")
			.AppendLine($"\tusing var validationContext = {Consts.ValidationContextGlobalRef}.Create(this);")
			.AppendLine(
				$"\treturn await (({Consts.InternalValidationInvokerGlobalRef})this).Validate(validationContext, serviceProvider);"
			)
			.AppendLine("}");

		var serviceProviderDep = dependencies.Services.Count > 0 ? "serviceProvider" : "null";

		// Validate() method
		var validateReturnType = isAsync
			? $"ValueTask<{Consts.ValidationResultGlobalRef}>"
			: Consts.ValidationResultGlobalRef;
		var overrideVirtual = properties.Object.InheritsValidatableObject ? "override" : "virtual";

		validateMethodFilePart
			.AppendLine(
				"""

				/// <summary>
				/// Validate the object and get the result with error messages.
				/// </summary>
				/// <returns>Returns disposable ValidationResult.</returns>
				""".Indent(1)
			)
			// > public virtual async ValueTask<ValidationResult> Validate(
			.Append($"public {overrideVirtual} {asyncKeyword}{validateReturnType} Validate(")
			.AppendIf($"{Consts.ServiceProviderGlobalRef} serviceProvider", dependencies.Services.Count > 0)
			.AppendLine(")")
			.AppendLine("{")
			.AppendLine($"\tusing var validationContext = {Consts.ValidationContextGlobalRef}.Create(this);")
			.AppendLineIf(
				$"\treturn await (({Consts.InternalValidationInvokerGlobalRef})this).Validate(validationContext, {serviceProviderDep});",
				isAsync
			)
			.AppendLineIf(
				$"\treturn (({Consts.InternalValidationInvokerGlobalRef})this).Validate(validationContext, {serviceProviderDep}).Result;",
				!isAsync
			)
			.AppendLine("}");

		return validateMethodFilePart;
	}

	private static void AppendBeforeValidateHook(
		ObjectProperties objectProperties,
		SourceTextSectionBuilder validateMethodSourceTextSection,
		bool isAsync
	)
	{
		if (objectProperties.BeforeValidateMethod is null)
		{
			return;
		}

		string awaitKeyword =
			(objectProperties.BeforeValidateMethod.ReturnTypeType & ReturnTypeType.Awaitable) != 0
				? "await "
				: string.Empty;

		var arguments = string.Join(
			", ",
			objectProperties.BeforeValidateMethod.Dependencies.Select(service => $"service{service}")
		);

		// VOID
		if (
			(objectProperties.BeforeValidateMethod.ReturnTypeType & ReturnTypeType.Void) != 0
			&& objectProperties.BeforeValidateMethod.ReturnTypeGenericArgument is null
		)
		{
			validateMethodSourceTextSection
				.AppendLine("// BEFORE Validate()")
				.AppendLine($"\t{awaitKeyword}BeforeValidate({arguments});")
				.AppendLine();

			return;
		}

		// ValidationResult
		if ((objectProperties.BeforeValidateMethod.ReturnTypeType & ReturnTypeType.ValidationResult) != 0)
		{
			validateMethodSourceTextSection
				.AppendLine("\t// BEFORE Validate()")
				.AppendLine($"\tvar beforeValidate = {awaitKeyword}BeforeValidate({arguments});")
				.AppendLine()
				.AppendLine("\tif (beforeValidate is not null)")
				.AppendLineIf("\t\treturn beforeValidate;", isAsync)
				.AppendLineIf(
					$"\t\treturn new ValueTask<{Consts.ValidationResultGlobalRef}>(beforeValidate);",
					!isAsync
				)
				.AppendLine();

			return;
		}

		// IEnumerable | IAsyncEnumerable
		validateMethodSourceTextSection
			.AppendLine("\t// BEFORE Validate()")
			.AppendLine($"\t{awaitKeyword}result.AddGlobalMessages(BeforeValidate({arguments}));")
			.AppendLine();
	}

	private static bool AppendAfterValidateHook(
		ObjectProperties objectProperties,
		SourceTextSectionBuilder validateMethodSourceTextSection
	)
	{
		if (objectProperties.AfterValidateMethod is null)
		{
			return false;
		}

		var arguments = string.Join(
			", ",
			objectProperties.AfterValidateMethod.Dependencies.Select(service => $"service{service}")
		);

		if ((objectProperties.AfterValidateMethod.ReturnTypeType & ReturnTypeType.Void) != 0)
		{
			string afterValidateAwaitKeyword =
				(objectProperties.AfterValidateMethod.ReturnTypeType & ReturnTypeType.Awaitable) != 0
					? "await "
					: string.Empty;

			validateMethodSourceTextSection
				.AppendLine("\t// AFTER Validate()")
				.AppendLine($"\t{afterValidateAwaitKeyword}AfterValidate({arguments});")
				.AppendLine();

			return false;
		}

		if ((objectProperties.AfterValidateMethod.ReturnTypeType & ReturnTypeType.AsyncEnumerable) != 0)
		{
			validateMethodSourceTextSection.AppendLine(
				$"\treturn await {Consts.ValidationResultHelperGlobalRef}.ReplaceOrAddMessages(result, AfterValidate({arguments}));"
			);
		}
		else if ((objectProperties.AfterValidateMethod.ReturnTypeType & ReturnTypeType.Awaitable) != 0)
		{
			validateMethodSourceTextSection.AppendLine(
				$"\treturn {Consts.ValidationResultHelperGlobalRef}.ReplaceOrAddMessages(result, await AfterValidate({arguments}));"
			);
		}
		else
		{
			validateMethodSourceTextSection.AppendLine(
				$"\treturn new ValueTask<{Consts.ValidationResultGlobalRef}>({Consts.ValidationResultHelperGlobalRef}.ReplaceOrAddMessages(result, AfterValidate({arguments})));"
			);
		}

		return true;
	}

	private static void ProcessValidatableProperty(
		PropertyProperties validatableProperty,
		EquatableArray<ValidatorProperties> validators,
		List<AttributeProperties> attributes,
		RulesClassBuilder rulesClassBuilder,
		CustomValidationInterfaceBuilder customValidationInterfaceBuilder,
		PropertiesValidationInvocationBuilder invocationBuilder
	)
	{
		var attributesWithValidators = new List<(AttributeProperties, ValidatorProperties)>(attributes.Count);
		var propertyCalls = new CallsCollection();

		foreach (var validationAttribute in attributes)
		{
			// CUSTOM validation
			if (validationAttribute.QualifiedName == Consts.CustomValidationAttribute)
			{
				customValidationInterfaceBuilder.AddCustomValidationForProperty(validatableProperty, propertyCalls);
				continue;
			}

			var validator = validators.FirstOrDefault(validator =>
				validator.QualifiedName == validationAttribute.QualifiedName
			);

			if (validator is null)
			{
				continue;
			}

			attributesWithValidators.Add((validationAttribute, validator));
		}

		// Generate RULE
		rulesClassBuilder.AddRuleForProperty(validatableProperty, attributesWithValidators);

		// Generate INVOCATION
		invocationBuilder.AddInvocationForProperty(validatableProperty, attributesWithValidators, propertyCalls);
	}

	private static void AddAutoValidators(
		List<AttributeProperties> attributes,
		PropertyProperties validatableProperty,
		ValigatorConfiguration config,
		bool? useAutoValidators
	)
	{
		// Auto validators are disabled for this object
		if (useAutoValidators == false)
		{
			return;
		}

		// Required - Add Required validator if the property is not nullable
		if (
			(useAutoValidators ?? config.AutoRequired)
			&& !validatableProperty.Nullable
			&& attributes.All(x => x.QualifiedName != Consts.RequiredAttributeQualifiedName)
			&& validatableProperty.PropertyTypeKind is not TypeKind.Enum and not TypeKind.Struct
		)
		{
			attributes.Add(AttributeProperties.Required);
		}

		// InEnum - Add InEnum validator for Enum properties
		if (
			(useAutoValidators ?? config.AutoInEnum)
			&& validatableProperty.PropertyTypeKind == TypeKind.Enum
			&& attributes.All(x => x.QualifiedName != Consts.InEnumAttributeQualifiedName)
		)
		{
			attributes.Add(AttributeProperties.InEnum);
		}
	}

	private static string CreateAddChain(List<(string Invocation, string Comment)> invocations)
	{
		return string.Join(
			Environment.NewLine + "\t\t\t\t",
			invocations.Select(invocation => $".AddValidationMessage({invocation.Invocation}) // {invocation.Comment}")
		);
	}

	private static string CreateConcatenation(List<string> enumerators)
	{
		if (enumerators.Count == 0)
		{
			return string.Empty;
		}

		if (enumerators.Count == 1)
		{
			return enumerators[0];
		}

		var builder = new StringBuilder();
		builder.AppendLine(enumerators[0]);

		for (int i = 1; i < enumerators.Count; i++)
		{
			builder.AppendLine($"\t\t\t\t\t\t.Concat({enumerators[i].TrimStart()})");
		}

		return builder.ToString();
	}
}
