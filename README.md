# DependencyInventory

CLI .NET 10 / Spectre.Console.Cli pour inventorier les références NuGet déclarées dans les dépôts Git d'un projet Azure DevOps ou d'un groupe GitLab.

Architecture : provider Git → sélection d'une branche → extraction NuGet → filtres glob/regex → rapports. Il n'existe aucun filtre implicite : un ou plusieurs patterns ou regex sont obligatoires.

## Azure DevOps

L'URL désigne le projet Azure DevOps, et non un dépôt Git.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organisation/Projet" --credentials-file --pattern "Stime.*"
~~~

Le fichier .azure-credentials dans le dossier courant contient deux lignes :

~~~text
prenom.nom@entreprise.fr
PAT_AZURE_DEVOPS
~~~

Le PAT Azure DevOps doit avoir le droit Code (Read).

## GitLab

L'URL désigne le groupe GitLab, éventuellement un sous-groupe, par exemple https://gitlab.example.com/red/web. Les projets des sous-groupes sont inclus.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider gitlab --url "https://gitlab.example.com/red/web" --credentials-file --pattern "Stime.*"
~~~

Le fichier .gitlab-credentials dans le dossier courant contient une seule ligne :

~~~text
PAT_GITLAB
~~~

Utiliser un PAT GitLab avec le scope read_api et les droits d'accès au groupe. Le fournisseur GitLab utilise PRIVATE-TOKEN ; aucun login supplémentaire n'est requis.

## Authentification

L'option --credentials-file est facultative.

- Sans l'option, le CLI demande les identifiants en mode interactif.
- Avec --credentials-file sans valeur, il lit .azure-credentials ou .gitlab-credentials selon --provider, dans le dossier courant.
- Avec --credentials-file suivi d'un chemin, il utilise ce chemin explicite (p. ex. "C:\Secrets\mytoken.txt").

Les fichiers de credentials contiennent des secrets en clair : ne pas les commiter, limiter leurs permissions et utiliser des jetons en lecture seule. Les fichiers par défaut sont exclus par .gitignore.

## Filtrage NuGet

Options répétables et combinées par OU :
- --pattern "Stime.*" : glob appliqué à l'identifiant complet. * = zéro ou plusieurs caractères, ? = exactement un caractère. Les points sont littéraux.
- --regex "^Serilog([.]|$)" : regex .NET, recherche de sous-chaîne (sauf ancres).
- --case-sensitive : optionnel, la recherche est insensible à la casse par défaut.

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organisation/Projet" --credentials-file --pattern "Stime.*" --pattern "Microsoft.Extensions.*"

dotnet run --project ./src/DependencyInventory -- --provider gitlab --url "https://gitlab.example.com/red/web" --credentials-file --regex "^Serilog([.]|$)"

dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organisation/Projet" --credentials-file --pattern "*.Hosting" --regex "^Stime[.]Core[.]"
~~~

Chaque dépendance apparaît une seule fois par déclaration, même si elle correspond à plusieurs critères. La colonne MatchedFilters conserve les critères qui ont trouvé le package.

## Sélection des branches

Une seule branche analysée par dépôt, dans l'ordre main → release (y compris release/*) → develop → dev → première autre branche (alphabétique). Pas de repli sur une seconde branche en cas d'absence de dépendances.

Les API Git Azure DevOps et GitLab permettent de lire le même commit de manière cohérente, sans clone local.

## Limites de requêtes

Quand Azure DevOps ou GitLab répond HTTP 429 (trop de requêtes), la requête est réessayée jusqu'à 5 tentatives au total. L'attente suit l'en-tête Retry-After s'il est présent, sinon un délai exponentiel avec une part aléatoire ; une attente ne dépasse jamais 60 s et un message l'annonce. Au-delà, l'erreur est reportée comme avant dans repositories.csv. Le délai de 120 s s'applique à chaque tentative, sans compter les attentes.

## Extraction NuGet

Fichiers : *.csproj, *.fsproj, *.vbproj, *.props, *.targets et packages.config. Dossiers bin, obj, node_modules et .git ignorés.

Les PackageReference, GlobalPackageReference et package sont des références directes ; PackageVersion constitue une déclaration de version centralisée, pas une preuve de consommation directe.

Le scanner peut rapprocher une version depuis le Directory.Packages.props le plus proche, sans évaluer complètement MSBuild. Il ne résout pas les dépendances transitives et n'exécute pas de restauration NuGet.

## Rapports

Le dossier dependency-report (modifiable avec --output) contient :

- dependencies.csv : références directes filtrées, package, version, fichier, branche, commit, MatchedFilters ;
- central-versions.csv : déclarations de versions centralisées correspondant aux critères ;
- repositories.csv : couverture de tous les dépôts découverts et statut d'analyse ;
- report.json : rapport JSON complet, incluant les filtres utilisés.

CSV UTF-8 BOM séparés par point-virgule.

## Diagnostic d'un dépôt

~~~powershell
dotnet run --project ./src/DependencyInventory -- --provider azure --url "https://dev.azure.com/Organisation/Projet" --credentials-file --pattern "Stime.*" --repository api-order-bo --diagnostics
~~~

Le fichier diagnostics.json expose toutes les références NuGet extraites, même hors filtre, les branches disponibles, celle sélectionnée, les commits et les fichiers examinés.

## Build / tests

~~~powershell
dotnet build ./src/DependencyInventory/DependencyInventory.csproj
dotnet test ./tests/DependencyInventory.Tests/DependencyInventory.Tests.csproj
~~~

## Messages de commit

Les commits suivent [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) et sont rédigés en anglais : `<type>[(scope)][!]: <description>`, avec un type parmi feat, fix, build, chore, ci, docs, perf, refactor, revert, style, test, une description en minuscule à l'impératif, sans point final, et un en-tête de 72 caractères au plus. Un changement cassant se signale par un `!` avant les deux-points, par un pied `BREAKING CHANGE:` (ou `BREAKING-CHANGE:`), ou par les deux.

Le workflow commit-lint vérifie chaque commit d'une pull request. Pour la même vérification en local, activer le hook une fois par clone :

~~~powershell
git config core.hooksPath .githooks
~~~

## Références

- https://learn.microsoft.com/en-us/rest/api/azure/devops/git/
- https://docs.gitlab.com/api/groups/
- https://docs.gitlab.com/api/branches/
- https://docs.gitlab.com/api/repositories/
- https://docs.gitlab.com/api/repository_files/
