using System.Net;
using System.Net.Mail;
using System.Text;
using School.Shared;

namespace School.Api;

public sealed class AccountMail(IConfiguration config, IWebHostEnvironment environment)
{
    public async Task Send(string recipient, string purpose, string token, CancellationToken ct)
    {
        var origin = config["PublicOrigin"] ?? throw new InvalidOperationException("PublicOrigin required");
        var link = origin.TrimEnd('/') + "/#" + purpose + "=" + Uri.EscapeDataString(token);
        var subject = purpose == "verify" ? "Verify your School Portal email" : "Reset your School Portal password";
        var text = $"{subject}\n\nOpen this link within 30 minutes:\n{link}\n\nIf you did not request this, ignore this email.";
        if (config["Mail:Mode"] == "File" && environment.IsDevelopment())
        {
            var directory = config["Mail:Directory"] ?? throw new InvalidOperationException("Mail directory required");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, Guid.NewGuid() + ".txt"), $"To: {recipient}\nSubject: {subject}\n\n{text}", Encoding.UTF8, ct);
            return;
        }
        if (config["Mail:Mode"] == "Resend")
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.Authorization = new("Bearer",config["Mail:ApiKey"]);
            try
            {
                using var response = await http.PostAsJsonAsync("https://api.resend.com/emails",new { from = config["Mail:From"], to = new[] { recipient }, subject, text },ct);
                if (!response.IsSuccessStatusCode) throw new Rejection(503,"MAIL_UNAVAILABLE");
            }
            catch (HttpRequestException) { throw new Rejection(503,"MAIL_UNAVAILABLE"); }
            return;
        }
        using var client = new SmtpClient(config["Mail:Host"], config.GetValue("Mail:Port", 587))
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(config["Mail:Username"], config["Mail:Password"]),
            Timeout = 8000
        };
        using var mail = new MailMessage(config["Mail:From"]!, recipient, subject, text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { await client.SendMailAsync(mail, deadline.Token); }
        catch (SmtpException) { throw new Rejection(503, "MAIL_UNAVAILABLE"); }
    }
}
