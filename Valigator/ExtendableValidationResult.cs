namespace Valigator;

/// <summary>
/// Represents the result of a validation. Variant with methods for adding global messages and properties results.
/// </summary>
public class ExtendableValidationResult : ValidationResult
{
	/// <summary>
	/// Represents the result of a validation. Variant with methods for adding global messages and properties results.
	/// </summary>
	/// <param name="globalMessages"></param>
	/// <param name="propertiesResult"></param>
	public ExtendableValidationResult(
		IEnumerable<ValidationMessage> globalMessages,
		params PropertyValidationResult[] propertiesResult
	)
		: base(globalMessages.ToList(), propertiesResult)
	{
		// Convert to list so we can add to it
		PropertiesResult = PropertiesResult.ToList();
	}

	/// <summary>
	/// Add a global message to the validation result
	/// </summary>
	/// <param name="message"></param>
	/// <returns></returns>
	public ExtendableValidationResult AddGlobalMessage(ValidationMessage message)
	{
		GlobalMessages.Add(message);
		return this;
	}

	/// <summary>
	/// Add a property result to the validation result
	/// </summary>
	public ExtendableValidationResult AddPropertyResult(PropertyValidationResult propertyResult)
	{
		PropertiesResult.Add(propertyResult);
		return this;
	}
}
