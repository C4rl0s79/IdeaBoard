# IdeaBoard

Self-hosted kanban na pomysły do rozważenia i ewentualnego wdrożenia — z projektami, osobą
odpowiedzialną za pomysł i jego wdrożenie oraz logowaniem kontami Google na zaproszenie.

Jeden katalog = cała aplikacja: `IdeaBoard.exe` (self-contained, z wbudowanym runtime .NET
i interfejsem), `appsettings.json` i `data\`. Na docelowej maszynie nie trzeba niczego instalować.

## Zawartość katalogu aplikacji

| Plik | Co to jest |
|---|---|
| `IdeaBoard.exe` | serwer + UI (Windows x64) |
| `appsettings.json` | konfiguracja: adres, Google, logowanie hasłem |
| `install-service.ps1` / `uninstall-service.ps1` | instalacja / usunięcie usługi Windows |
| `update.ps1` | aktualizacja do najnowszego wydania z GitHuba |
| `data\board.json` | tablica: kolumny, projekty, pomysły (tworzona przy pierwszym starcie) |
| `data\users.json` | zaproszeni użytkownicy i ich role |
| `data\settings.json` | reguły domen (kto wchodzi z jakiej domeny, blokada innych) |
| `data\backups\` | dzienna kopia tablicy, 30 ostatnich dni |
| `data\auth.json` | hash hasła konta awaryjnego (PBKDF2-SHA256) |
| `data\keys\` | klucze sesji (szyfrowane DPAPI dla tej maszyny) |

Kopia zapasowa = skopiuj `data\` (i `appsettings.json`, bo jest w nim sekret Google).
Przeniesienie na inną maszynę = skopiuj cały katalog; `data\keys` z innej maszyny nie da się
odszyfrować — usuń go, wszyscy zalogują się ponownie.

## Instalacja na serwerze

1. Skopiuj katalog aplikacji, np. do `C:\Apps\IdeaBoard`.
2. W PowerShell **jako administrator**:
   ```powershell
   cd C:\Apps\IdeaBoard
   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
   ```
   Skrypt zapyta o hasło konta awaryjnego (min. 10 znaków), nada kontu `LOCAL SERVICE`
   prawo zapisu w katalogu aplikacji (żeby mogła się sama aktualizować; poza nim nic),
   zarejestruje usługę `IdeaBoard` (start automatyczny, restart po awarii) i ją uruchomi.
   Instalację najłatwiej zacząć od paczki `IdeaBoard-X.Y.Z-win-x64.zip` z
   [wydań na GitHubie](https://github.com/C4rl0s79/IdeaBoard/releases).
3. Sprawdź lokalnie: `http://127.0.0.1:5077`.

Ręczne uruchomienie bez usługi (np. test): `.\IdeaBoard.exe` i Ctrl+C.
Po zmianie `appsettings.json`: `Restart-Service IdeaBoard`.

## Cloudflare Tunnel

Aplikacja słucha tylko na `127.0.0.1`, więc z zewnątrz dostępna jest wyłącznie przez tunel.
W konfiguracji tunelu (`config.yml` albo panel Zero Trust → Tunnels → Public Hostname):

```yaml
ingress:
  - hostname: pomysly.twojadomena.pl
    service: http://127.0.0.1:5077
  - service: http_status:404
```

W `appsettings.json` wpisz publiczny adres — z niego budowany jest adres powrotu z Google:

```json
"PublicUrl": "https://pomysly.twojadomena.pl"
```

## Logowanie

Są dwie metody, można włączyć obie:

- **Google** (dla zespołu) — wchodzi tylko adres zaproszony przez admina.
- **Hasło** (konto awaryjne „Administrator”) — działa bez Google, np. do pierwszej
  konfiguracji. Wyłączysz je przez `"PasswordLogin": false`. Zmiana hasła:
  `.\IdeaBoard.exe set-password` (wylogowuje sesje tego konta).

### Konfiguracja Google (jednorazowo)

1. [console.cloud.google.com](https://console.cloud.google.com) → utwórz projekt (np. „IdeaBoard”).
2. **APIs & Services → OAuth consent screen**: typ **External**, nazwa aplikacji, e-mail
   kontaktowy. Zakresy domyślne (`openid`, `email`, `profile`) — nie wymagają weryfikacji.
   Na końcu **Publish app** („In production”); w trybie „Testing” wejdą tylko konta dopisane
   tam jako testowe.
3. **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   - Application type: **Web application**
   - Authorized redirect URIs: `https://pomysly.twojadomena.pl/signin-google`
4. Skopiuj Client ID i Client secret do `appsettings.json`:
   ```json
   "Google": {
     "ClientId": "1234-abc.apps.googleusercontent.com",
     "ClientSecret": "GOCSPX-..."
   }
   ```
5. `Restart-Service IdeaBoard` — na ekranie logowania pojawi się „Zaloguj przez Google”.

### Użytkownicy i role

| Rola | Może |
|---|---|
| `admin` | wszystko + zapraszanie użytkowników, role, kolumny |
| `editor` | dodawać i zmieniać pomysły, notatki, projekty |
| `viewer` | tylko przeglądać |
| `blocked` (zablokowany) | nic — nie zaloguje się, nawet z otwartej domeny |

Zapraszanie w aplikacji: **Użytkownicy → Zaproś** (adres konta Google + rola). Po dodaniu
dostajesz gotowy tekst zaproszenia z linkiem — „Otwórz w poczcie” albo „Kopiuj tekst”.
Aplikacja sama nie wysyła maili.

To samo z konsoli na serwerze (działa też przy uruchomionej usłudze):

```powershell
.\IdeaBoard.exe user add jan.kowalski@gmail.com editor
.\IdeaBoard.exe user role jan.kowalski@gmail.com admin
.\IdeaBoard.exe user remove jan.kowalski@gmail.com
.\IdeaBoard.exe user list
```

Usunięcie użytkownika kończy jego sesje od razu.

### Domeny

W **Użytkownicy → Domeny** dodajesz domeny pocztowe, np. `pyrkon.pl`, i dla każdej wybierasz:

- **każdy jako viewer** (albo editor) — każdy z tej domeny może się zalogować bez zaproszenia;
  przy pierwszym logowaniu trafia na listę z tą rolą, potem admin może mu ją podnieść.
  Żeby odciąć konkretną osobę z otwartej domeny, ustaw jej rolę **zablokowany**
  (samo usunięcie nie wystarczy — wróciłaby przy następnym logowaniu).
- **tylko zaproszeni** — domena jest dozwolona, ale wchodzą tylko adresy z listy.

**Blokuj logowanie Google spoza tych domen** — po włączeniu wpuszczane są tylko adresy
z wymienionych domen, także spośród zaproszonych; trwające sesje spoza nich wygasają od razu.
Przy jednej domenie Google od razu proponuje tylko konta z niej. Konto hasła działa dalej.
Odrzucone próby (kto, kiedy, dlaczego) widać na dole panelu Użytkownicy.

Z konsoli:

```powershell
.\IdeaBoard.exe domain add pyrkon.pl viewer      # viewer | editor | invited (tylko zaproszeni)
.\IdeaBoard.exe domain restrict on               # on | off
.\IdeaBoard.exe domain remove pyrkon.pl
.\IdeaBoard.exe domain list
```

Adres z domeny musi być potwierdzony przez Google (`email_verified`) — czyli właściciel konta
ma dostęp do tej skrzynki.

## Obsługa

- **+ Pomysł** lub klawisz `N` — nowy pomysł; `+ Dodaj pomysł` pod kolumną — szybkie dodanie samego tytułu.
- Karta: **projekt**, **odpowiedzialny** (za pomysł i wdrożenie), kolumna, priorytet, tagi,
  opis, notatki z autorem i datą, historia zmian (kto, co, kiedy).
- Filtry w nagłówku: projekt, odpowiedzialny (w tym **Moje** i nieprzypisane), tagi, `/` — szukaj.
  Nowy pomysł dodany przy włączonym filtrze dostaje ten projekt / osobę.
- **Projekty** — dodawanie, zmiana nazwy, usuwanie (pomysły zostają bez projektu).
- Przeciąganie kart myszą; na telefonie przytrzymaj kartę ~0,3 s i przeciągnij.
- Kolumny (admin): kliknij nazwę, aby zmienić; `‹ ›` przesuwa, `✕` usuwa pustą.
- **Eksport** pobiera całą tablicę jako JSON.

## Aktualizacje

Nowe wersje są publikowane jako [wydania na GitHubie](https://github.com/C4rl0s79/IdeaBoard/releases)
(paczka zip + suma SHA-256).

- **Z aplikacji** — admin widzi w nagłówku przycisk **Aktualizacja X.Y.Z**. Serwer pobiera paczkę,
  sprawdza sumę kontrolną, podmienia pliki (bez `appsettings.json` i `data\`) i restartuje usługę;
  strona przeładuje się sama po kilku sekundach.
- **Ręcznie** — w PowerShell jako administrator, w katalogu aplikacji:
  ```powershell
  powershell -ExecutionPolicy Bypass -File .\update.ps1
  ```
  Ten sam skrypt nadaje uprawnienia i ustawienia usługi, których potrzebuje przycisk w aplikacji —
  przy przejściu ze starszej wersji (bez aktualizacji) uruchom go raz ręcznie.

Poprzedni `IdeaBoard.exe` zostaje na chwilę jako `IdeaBoard.exe.old` i jest usuwany, gdy nowa
wersja wystartuje. Jeśli nowa wersja nie wstaje: zatrzymaj usługę, zmień nazwę `.old` z powrotem
na `IdeaBoard.exe` i uruchom usługę.

## Bezpieczeństwo

- Słucha tylko na loopback; `X-Forwarded-*` przyjmowane tylko od loopback (`cloudflared`).
- Cookie sesji `HttpOnly`, `SameSite=Lax`, `Secure` przy HTTPS; zapisy wymagają nagłówka
  `X-IdeaBoard` (ochrona CSRF); role sprawdzane po stronie serwera.
- Google: wpuszczane tylko zweryfikowane adresy z listy zaproszonych.
- Hasło: blokada adresu (`CF-Connecting-IP`) na 15 min po 5 błędnych próbach.

## Budowanie (maszyna deweloperska, .NET SDK 10)

```powershell
.\scripts\publish.ps1                       # -> dist\IdeaBoard\
.\scripts\publish.ps1 -Out \\serwer\Apps\IdeaBoard
```

Wydanie nowej wersji: podbij `<Version>` w `src\IdeaBoard\IdeaBoard.csproj`, zrób commit na `main`
i uruchom `.\scripts\release.ps1 -Notes "Co nowego"` — zbuduje paczkę, wypchnie tag `vX.Y.Z`
i utworzy wydanie na GitHubie (wymaga `gh`).

Publikacja na istniejący katalog nie nadpisuje `appsettings.json` ani `data\`.
Przy aktualizacji zatrzymaj najpierw usługę na serwerze (`Stop-Service IdeaBoard`), podmień
pliki i uruchom ją ponownie. Aktualizując z wersji bez Google, dopisz do swojego
`appsettings.json` klucze `PublicUrl`, `Google` i `PasswordLogin` (wzór w `src\IdeaBoard\appsettings.json`).
