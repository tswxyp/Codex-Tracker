using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.WebUtilities;

namespace Tracker.Server;
public class LoginInput
{
    [Required,EmailAddress,StringLength(254)] public string Email {get;set;}="";
    [Required,StringLength(1024)] public string Password {get;set;}="";
}
public sealed class RegisterInput:LoginInput
{
    [Required,Compare(nameof(Password))]public string ConfirmPassword {get;set;}="";
}
[AllowAnonymous]
public sealed class LoginPage(SignInManager<IdentityUser> signin):PageModel
{
    [BindProperty]public LoginInput Input{get;set;}=new();
    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        if(!ModelState.IsValid)return Page();
        var result=await signin.PasswordSignInAsync(Input.Email.Trim(),Input.Password,false,lockoutOnFailure:true);
        var dest=Url.IsLocalUrl(returnUrl)?returnUrl!:"/";
        if(result.Succeeded)return LocalRedirect(dest);
        if(result.RequiresTwoFactor)return RedirectToPage("/Account/LoginWith2fa",new {area="Identity",returnUrl=dest});
        ModelState.AddModelError("","登录失败，请核对邮箱、密码与验证状态。");return Page();
    }
}
[AllowAnonymous]
public sealed class RegisterPage(UserManager<IdentityUser> users,IEmailSender mail,Settings settings):PageModel
{
    [BindProperty]public RegisterInput Input{get;set;}=new();
    public async Task<IActionResult> OnPostAsync()
    {
        if(!ModelState.IsValid)return Page();
        string address=Input.Email.Trim();var user=new IdentityUser{UserName=address,Email=address};
        var result=await users.CreateAsync(user,Input.Password);
        if(result.Succeeded){
            string token=WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await users.GenerateEmailConfirmationTokenAsync(user)));
            var path=QueryHelpers.AddQueryString("/Identity/Account/ConfirmEmail",new Dictionary<string,string?>{["userId"]=user.Id,["code"]=token});
            var link=WebUtility.HtmlEncode(new Uri(settings.Issuer,path).AbsoluteUri);
            await mail.SendEmailAsync(address,"Codex Tracker 邮箱验证",$"<a href='{link}'>验证邮箱</a>");
        }else if(result.Errors.Any(e=>e.Code is not ("DuplicateUserName" or "DuplicateEmail"))){
            foreach(var e in result.Errors.Where(e=>e.Code is not ("DuplicateUserName" or "DuplicateEmail")))ModelState.AddModelError("",e.Description);
            return Page();
        }
        return new ContentResult{StatusCode=202,ContentType="text/html; charset=utf-8",
            Content="<html lang='zh'><meta charset='utf-8'><p>请检查邮箱。如已注册，可登录或找回密码。</p><a href='/Identity/Account/Login'>登录</a></html>"};
    }
}
