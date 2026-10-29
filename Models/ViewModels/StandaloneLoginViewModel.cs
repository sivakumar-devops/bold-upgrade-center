using System.ComponentModel.DataAnnotations;

namespace Bold.UpgradeCenter.Models.ViewModels;

public sealed class StandaloneLoginViewModel
{
    [Required]
    [StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(4096)]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
