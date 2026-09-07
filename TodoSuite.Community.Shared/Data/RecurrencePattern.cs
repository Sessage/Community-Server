namespace Klassenbibliothek.Data;

public enum RecurrencePattern
{
    Keine,
    Taeglich,
    Woechentlich,
    BestimmteWochentage,
    Monatlich,
    Jaehrlich,
    Benutzerdefiniert,
    // Append-only: enum integers are persisted in existing databases.
    TageNachAbschluss
}
