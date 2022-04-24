﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Fido2NetLib.Objects;
using Fido2NetLib;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static Fido2NetLib.Fido2;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Fido2Identity;

[Route("api/[controller]")]
public class PwFido2RegisterController : Controller
{
    private readonly Fido2 _lib;
    public static IMetadataService _mds;
    private readonly Fido2Storage _fido2Storage;
    private readonly UserManager<IdentityUser> _userManager;
    private readonly IOptions<Fido2Configuration> _optionsFido2Configuration;


    public PwFido2RegisterController(
        Fido2Storage fido2Storage,
        UserManager<IdentityUser> userManager,
        IOptions<Fido2Configuration> optionsFido2Configuration)
    {
        _userManager = userManager;
        _optionsFido2Configuration = optionsFido2Configuration;
        _fido2Storage = fido2Storage;

        _lib = new Fido2(new Fido2Configuration()
        {
            ServerDomain = _optionsFido2Configuration.Value.ServerDomain,
            ServerName = _optionsFido2Configuration.Value.ServerName,
            Origin = _optionsFido2Configuration.Value.Origin,
            TimestampDriftTolerance = _optionsFido2Configuration.Value.TimestampDriftTolerance
        });
    }

    private string FormatException(Exception e)
    {
        return string.Format("{0}{1}", e.Message, e.InnerException != null ? " (" + e.InnerException.Message + ")" : "");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("/pwmakeCredentialOptions")]
    public async Task<JsonResult> MakeCredentialOptions([FromForm] string username, [FromForm] string displayName, [FromForm] string attType, [FromForm] string authType, [FromForm] bool requireResidentKey, [FromForm] string userVerification)
    {
        try
        {
            if (string.IsNullOrEmpty(username))
            {
                username = $"{displayName} (Usernameless user created at {DateTime.UtcNow})";
            }

            var user = new Fido2User
            {
                DisplayName = displayName,
                Name = username,
                Id = Encoding.UTF8.GetBytes(username) // byte representation of userID is required
            };

            // 2. Get user existing keys by username
            var items = await _fido2Storage.GetCredentialsByUserName(username);
            var existingKeys = new List<PublicKeyCredentialDescriptor>();
            foreach (var publicKeyCredentialDescriptor in items)
            {
                existingKeys.Add(publicKeyCredentialDescriptor.Descriptor);
            }

            // 3. Create options
            var authenticatorSelection = new AuthenticatorSelection
            {
                RequireResidentKey = requireResidentKey,
                UserVerification = userVerification.ToEnum<UserVerificationRequirement>()
            };

            if (!string.IsNullOrEmpty(authType))
                authenticatorSelection.AuthenticatorAttachment = authType.ToEnum<AuthenticatorAttachment>();

            var exts = new AuthenticationExtensionsClientInputs() { Extensions = true, UserVerificationIndex = true, Location = true, UserVerificationMethod = true, BiometricAuthenticatorPerformanceBounds = new AuthenticatorBiometricPerfBounds { FAR = float.MaxValue, FRR = float.MaxValue } };

            var options = _lib.RequestNewCredential(user, existingKeys, authenticatorSelection, attType.ToEnum<AttestationConveyancePreference>(), exts);

            // 4. Temporarily store options, session/in-memory cache/redis/db
            HttpContext.Session.SetString("fido2.attestationOptions", options.ToJson());

            // 5. return options to client
            return Json(options);
        }
        catch (Exception e)
        {
            return Json(new CredentialCreateOptions { Status = "error", ErrorMessage = FormatException(e) });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Route("/pwmakeCredential")]
    public async Task<JsonResult> MakeCredential([FromBody] AuthenticatorAttestationRawResponse attestationResponse)
    {
        try
        {
            // 1. get the options we sent the client
            var jsonOptions = HttpContext.Session.GetString("fido2.attestationOptions");
            var options = CredentialCreateOptions.FromJson(jsonOptions);

            // 2. Create callback so that lib can verify credential id is unique to this user
            IsCredentialIdUniqueToUserAsyncDelegate callback = async (IsCredentialIdUniqueToUserParams args) =>
            {
                var users = await _fido2Storage.GetUsersByCredentialIdAsync(args.CredentialId);
                if (users.Count > 0) return false;

                return true;
            };

            // 2. Verify and make the credentials
            var success = await _lib.MakeNewCredentialAsync(attestationResponse, options, callback);

            // 3. Store the credentials in db
            await _fido2Storage.AddCredentialToUser(options.User, new FidoStoredCredential
            {
                UserName = options.User.Name,
                Descriptor = new PublicKeyCredentialDescriptor(success.Result.CredentialId),
                PublicKey = success.Result.PublicKey,
                UserHandle = success.Result.User.Id,
                SignatureCounter = success.Result.Counter,
                CredType = success.Result.CredType,
                RegDate = DateTime.Now,
                AaGuid = success.Result.Aaguid
            });

            // 4. return "ok" to the client

            var user = await CreateUser(options.User.Name);
            // await _userManager.GetUserAsync(User);

            if (user == null)
            {
                return Json(new CredentialMakeResult { Status = "error", ErrorMessage = $"Unable to load user with ID '{_userManager.GetUserId(User)}'." });
            }

            //await _userManager.SetTwoFactorEnabledAsync(user, true);
            //var userId = await _userManager.FindByNameAsync(user);

            return Json(success);
        }
        catch (Exception e)
        {
            return Json(new CredentialMakeResult { Status = "error", ErrorMessage = FormatException(e) });
        }
    }

    private async Task<IdentityUser> CreateUser(string userEmail)
    {
        var user = new IdentityUser { UserName = userEmail, Email = userEmail, EmailConfirmed = true };
        var result = await _userManager.CreateAsync(user);
        if (result.Succeeded)
        {
            //await _signInManager.SignInAsync(user, isPersistent: false);
        }

        return user;
    }
}