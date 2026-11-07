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
