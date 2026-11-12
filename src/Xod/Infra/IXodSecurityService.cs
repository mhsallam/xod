using System;

namespace Xod.Infra
{
    public interface IXodTransaction : IDisposable
    {
        Guid TransactionId { get; }
        bool IsActive { get; }
        void Commit();
        void Rollback();
    }

    [Obsolete("Use XodContext.Secure, Loose and ChangePassword: the one way a database's password changes.")]
    public interface IXodSecurityService
    {
        event EventHandler Changed;
        string Password { get; set; }
        string Path { get; set; }

        void ChangePassword(string currentPassword, string newPassword);
        void Loose(string password);
        void Secure(string password);
    }
}
