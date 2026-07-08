using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace NCacheDataProtectionSample.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProtectionController : ControllerBase
{
    private readonly IDataProtector _protector;

    public ProtectionController(IDataProtectionProvider provider)
    {
        // "Purpose" strings scope keys so different features can't
        // decrypt each other's payloads even though they share the
        // same underlying key ring stored in NCache.
        _protector = provider.CreateProtector("ProtectionController.v1");
    }

    /// <summary>
    /// Encrypts the supplied plain text using a key ring whose keys
    /// are stored in and retrieved from NCache.
    /// GET /api/protection/protect?plainText=hello
    /// </summary>
    [HttpGet("protect")]
    public IActionResult Protect([FromQuery] string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return BadRequest("plainText query parameter is required.");

        var protectedPayload = _protector.Protect(plainText);
        var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? "Server";
        return Ok(new { protectedPayload, instanceName });
    }

    /// <summary>
    /// Decrypts a payload previously produced by /protect. Works even if
    /// this request lands on a different server instance, because every
    /// instance reads the same key ring from NCache.
    /// GET /api/protection/unprotect?protectedPayload=...
    /// </summary>
    [HttpGet("unprotect")]
    public IActionResult Unprotect([FromQuery] string protectedPayload)
    {
        if (string.IsNullOrEmpty(protectedPayload))
            return BadRequest("protectedPayload query parameter is required.");

        try
        {
            var plainText = _protector.Unprotect(protectedPayload);
            var instanceName = Environment.GetEnvironmentVariable("INSTANCE_NAME") ?? "Server";
            return Ok(new { plainText, instanceName });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Unable to unprotect payload.", detail = ex.Message });
        }
    }
}