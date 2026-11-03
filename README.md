# EventPass · back (API)

API REST de la billetterie EventPass : ASP.NET Core 8 (minimal APIs), Entity Framework Core, authentification JWT (rôles Client / Organisateur), documentation Swagger.

```bash
docker build -t eventpass-back .          # lance aussi les 7 tests d'intégration
docker run -d -p 5080:8080 eventpass-back # SQLite intégré, données de démonstration
```

- Documentation interactive : http://localhost:5080/api/docs
- Santé : `/health` (liveness) et `/health/ready` (readiness : 503 si la base est injoignable)
- Informations de déploiement (pod, moteur de base, persistance) : `/api/system/info`

## Points techniques

- **PostgreSQL ou SQLite** : PostgreSQL est utilisé si `DB_HOST` ou `DATABASE_URL` est défini. Sinon, SQLite est utilisé dans `SQLITE_PATH`, avec un repli dans `/tmp` si le dossier n'est pas inscriptible (mode lecture seule).
  Le schéma est créé au démarrage. Avec PostgreSQL, l'API attend jusqu'à environ 60 s que la base soit prête.
- **Aucune survente** : la jauge est décrémentée par un `UPDATE … WHERE Sold + n <= Capacity` atomique.
- **Contrôle d'accès** : le passage d'un billet de « valide » à « utilisé » est atomique. Deux scans simultanés ne laissent entrer qu'une seule personne.
- **Sécurité** :
  - mots de passe hachés (PBKDF2, ASP.NET Identity) ;
  - temps de réponse identique, que l'email existe ou non ;
  - pas d'en-tête `Server` ;
  - utilisateur non-root `1654`.
- **Image** : construction en plusieurs étapes. L'image n'est produite que si `dotnet test` réussit.

## Principales routes

| Méthode | Route | Accès |
|---|---|---|
| POST | `/api/auth/register`, `/api/auth/login` | public |
| GET | `/api/auth/me` | connecté |
| GET | `/api/events`, `/api/events/{id}`, `/api/events/filters` | public |
| POST | `/api/bookings` · GET `/api/bookings/mine` · POST `/api/bookings/{id}/cancel` | connecté |
| GET/POST/PUT/DELETE | `/api/admin/events` | organisateur |
| GET | `/api/admin/stats` | organisateur |
| POST | `/api/admin/checkin` · GET `/api/admin/checkins` | organisateur |

Les variables d'environnement sont décrites dans le [README principal](../README.md).
