using System;
namespace Xod.Infra
{
    interface IXodSecurityService
    {
        event EventHandler Changed;
        void ChangePassword(string currentPassword, string newPassword);
        void Loose(string password);
        string Password { get; set; }
        string Path { get; set; }
        void Secure(string password);
    }
}
