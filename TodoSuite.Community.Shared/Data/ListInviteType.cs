namespace Klassenbibliothek.Data;

/*
 * Token-Invite (für Link + Mail)
 * Wird vom neuen Share-Service verwaltet, gehört aber in den DB-Context.
 */
public enum ListInviteType
{
    ShareLink = 0,
    EmailInvite = 1
}
