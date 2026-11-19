using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity.UI.Services;
namespace Tracker.Server;

internal sealed class AccountMail(IConfiguration config) : IEmailSender
{
    public async Task SendEmailAsync(string email,string subject,string htmlMessage)
    {
        var c=config.GetSection("Smtp");
        using var smtp=new SmtpClient(c["Host"]??throw new InvalidOperationException("Configure SMTP."),c.GetValue("Port",587)){
            EnableSsl=c.GetValue("StartTls",true)
        };
        if(c["User"] is {Length:>0} login) smtp.Credentials=new NetworkCredential(login,c["Password"]?.TrimEnd('\r','\n'));
        using var msg=new MailMessage(c["From"]??throw new InvalidOperationException("Configure SMTP sender."),email,subject,htmlMessage){IsBodyHtml=true};
        await smtp.SendMailAsync(msg);
    }
}
