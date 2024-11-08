using Valigator.Validators;

namespace Valigator.Extensions.Validators.Numbers;

/// <summary>
/// Validator that checks if a value is greater than or equal to a specified minimum
/// </summary>
[Validator]
[ValidationAttribute(typeof(GreaterThanOrEqualAttribute))]
[ValidatorDescription("must be greater than or equal to {0}")]
public class GreaterThanOrEqualValidator : Validator
{
	private readonly decimal _min;

	/// <param name="min">The minimum value.</param>
	public GreaterThanOrEqualValidator(decimal min)
	{
		_min = min;
	}

	/// <param name="min">The minimum value.</param>
	public GreaterThanOrEqualValidator(int min)
	{
		_min = min;
	}

	/// <param name="min">The minimum value.</param>
	public GreaterThanOrEqualValidator(double min)
	{
		_min = (decimal)min;
	}

	/// <inheritdoc />
	public override IEnumerable<ValidationMessage> IsValid(object? value)
	{
		if (value is null)
		{
			yield break;
		}

		decimal decimalValue = Convert.ToDecimal(value);

		if (decimalValue < _min)
		{
			yield return new ValidationMessage(
				"Must be greater than or equal to {0}.",
				"Valigator.Validations.GreaterThanOrEqual",
				value
			);
		}
	}
}

#pragma warning disable CS1591 // Comment is on source-generated part
public partial class GreaterThanOrEqualAttribute;
#pragma warning restore CS1591
