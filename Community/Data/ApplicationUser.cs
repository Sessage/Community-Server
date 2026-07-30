using Microsoft.AspNetCore.Identity;

namespace Klassenbibliothek.Data
{
    // Add profile data for application users by adding properties to the ApplicationUser class
    public class ApplicationUser : IdentityUser
    {
        /// <summary>
        /// Relativer Pfad zum gespeicherten Profilbild (z. B. "profile-pictures/abc123.jpg").
        /// Null, wenn kein Profilbild hinterlegt ist.
        /// </summary>
        public string? ProfilePicturePath { get; set; }

        /// <summary>
        /// Explizit gewählte UI-Sprache. Null verwendet weiterhin die Sprache des
        /// Browsers beziehungsweise des Geräts.
        /// </summary>
        [PersonalData]
        public string? PreferredLanguage { get; set; }
    }

}
