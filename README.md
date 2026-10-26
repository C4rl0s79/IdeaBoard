# IdeaBoard

Self-hosted kanban na pomysły do rozważenia i ewentualnego wdrożenia.
Jeden katalog = cała aplikacja: `IdeaBoard.exe` (self-contained, z wbudowanym runtime .NET
i interfejsem), `appsettings.json` i `data\`. Na docelowej maszynie nie trzeba niczego instalować.

## Zawartość katalogu aplikacji

| Plik | Co to jest |
|---|---|
| `IdeaBoard.exe` | serwer + UI (Windows x64) |
| `appsettings.json` | adres nasłuchu (domyślnie `http://127.0.0.1:5077`) |
| `install-service.ps1` / `uninstall-service.ps1` | instalacja / usunięcie usługi Windows |
| `data\board.json` | tablica (tworzona przy pierwszym starcie) |
| `data\backups\` | dzienna kopia tablicy, 30 ostatnich dni |
| `data\auth.json` | hash hasła (PBKDF2-SHA256) |
| `data\keys\` | klucze sesji (szyfrowane DPAPI dla tej maszyny) |

Kopia zapasowa = skopiuj `data\`. Przeniesienie na inną maszynę = skopiuj cały katalog
(po przeniesieniu `data\keys` z innej maszyny nie da się odszyfrować — usuń go, trzeba będzie
się zalogować ponownie).

## Instalacja na serwerze

1. Skopiuj katalog aplikacji, np. do `C:\Apps\IdeaBoard`.
2. W PowerShell **jako administrator**:
   ```powershell
   cd C:\Apps\IdeaBoard
   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
   ```
   Skrypt zapyta o hasło (min. 10 znaków), nada uprawnienia kontu `LOCAL SERVICE`
   (odczyt katalogu, zapis tylko w `data\`), zarejestruje usługę `IdeaBoard`
   (start automatyczny, restart po awarii) i ją uruchomi.
3. Sprawdź lokalnie: `http://127.0.0.1:5077`.

Zmiana hasła (wylogowuje wszystkie sesje, usługa może działać):
```powershell
.\IdeaBoard.exe set-password
```

Ręczne uruchomienie bez usługi (np. test): `.\IdeaBoard.exe` i Ctrl+C.

## Cloudflare Tunnel

Aplikacja słucha tylko na `127.0.0.1`, więc z zewnątrz dostępna jest wyłącznie przez tunel.
W konfiguracji tunelu (`config.yml` albo panel Zero Trust → Tunnels → Public Hostname):

```yaml
ingress:
  - hostname: pomysly.twojadomena.pl
    service: http://127.0.0.1:5077
  - service: http_status:404
```

Aplikacja ufa nagłówkom `X-Forwarded-*` tylko od loopback (czyli od `cloudflared`), cookie
sesji dostaje flagę `Secure` przy HTTPS. Logowanie blokuje adres (`CF-Connecting-IP`) na
15 minut po 5 błędnych próbach. Opcjonalnie dołóż regułę rate limiting na `/api/login`
w Cloudflare WAF.

Inny port: zmień `Urls` w `appsettings.json` i zrestartuj usługę (`Restart-Service IdeaBoard`).

## Obsługa

- **+ Pomysł** lub klawisz `N` — nowy pomysł; `+ Dodaj pomysł` pod kolumną — szybkie dodanie samego tytułu.
- Przeciąganie kart myszą; na telefonie przytrzymaj kartę ~0,3 s i przeciągnij.
  Kolumnę można też zmienić w oknie karty.
- Karta: opis, tagi, priorytet, notatki (dopisywane przemyślenia z datą) i historia przeniesień.
- `/` — szukaj (tytuł, opis, tagi, notatki); kliknięcie tagu w nagłówku filtruje.
- Kolumny: kliknij nazwę, aby zmienić; `‹ ›` przesuwa, `✕` usuwa pustą.
- **Eksport** pobiera całą tablicę jako JSON.

## Budowanie (maszyna deweloperska, .NET SDK 10)

```powershell
.\scripts\publish.ps1                       # -> dist\IdeaBoard\
.\scripts\publish.ps1 -Out \\serwer\Apps\IdeaBoard
```

Publikacja na istniejący katalog nie nadpisuje `appsettings.json` ani `data\`.
Przy aktualizacji zatrzymaj najpierw usługę na serwerze (`Stop-Service IdeaBoard`), podmień
pliki i uruchom ją ponownie.
