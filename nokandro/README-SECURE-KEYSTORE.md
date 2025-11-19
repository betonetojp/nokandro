Security guidance ? keystore handling

Do not commit your Android keystore or passwords to version control.
Follow these steps to secure your keystore and keep it out of Git:

1) Move keystore to a secure location outside the repository
   - Recommended: place under a user-specific directory, e.g. `%USERPROFILE%\\.nokandro\\myrelease.jks` on Windows or `~/.nokandro/myrelease.jks` on Unix-like systems.

2) Add keystore path and secrets to local-only configuration
   - Use environment variables or a local `secrets.props` file excluded from Git:
     - `NOKANDRO_KEYSTORE_PATH` ? full path to `myrelease.jks`
     - `NOKANDRO_KEYSTORE_PASS` ? keystore password
     - `NOKANDRO_KEY_ALIAS` ? key alias
     - `NOKANDRO_KEY_PASS` ? key password

3) Example: `secrets.props` (DO NOT ADD TO GIT)
   ```xml
   <Project>
     <PropertyGroup>
       <AndroidSigningKeyStore>$(NOKANDRO_KEYSTORE_PATH)</AndroidSigningKeyStore>
       <AndroidSigningStorePass>$(NOKANDRO_KEYSTORE_PASS)</AndroidSigningStorePass>
       <AndroidSigningKeyAlias>$(NOKANDRO_KEY_ALIAS)</AndroidSigningKeyAlias>
       <AndroidSigningKeyPass>$(NOKANDRO_KEY_PASS)</AndroidSigningKeyPass>
     </PropertyGroup>
   </Project>
   ```

   - Add `secrets.props` to `.gitignore`.
   - Import `secrets.props` in your `.csproj` or pass properties via command line.

4) Use CI secrets to sign builds on a build server
   - Store keystore and passwords as secrets in your CI (GitHub Actions, Azure DevOps, etc.).
   - Use secure file/certificate features to retrieve keystore during the pipeline and pass passwords from secret variables.

5) Removing existing keystore from Git history
   - If the keystore was accidentally committed, remove it from the repository history (use `git filter-repo` or `git filter-branch`) and rotate passwords.

6) Keep an offline backup of the keystore and password in a secure vault (e.g. LastPass, 1Password) ? losing it prevents updating the app on Play Store.

Checklist to add to repo (developer action items)
- Add `%USERPROFILE%\\.nokandro` or `~/.nokandro` to your backup plan.
- Add `secrets.props` to `.gitignore` and create a `secrets.props.example` with placeholder values.
- Remove any checked-in keystore and rotate credentials if necessary.
