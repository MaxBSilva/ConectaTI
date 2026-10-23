namespace ConectaTI.App.Helpers;

public readonly record struct NormalizedUserIdentity(
    string RawUsername,
    string CleanUsername,
    string Domain,
    bool IsUpn,
    string EffectiveDownLevelLogonName,
    string EffectiveSshUsername);

/// <summary>
/// Trata corretamente usuários locais, DOMINIO\usuario e usuario@dominio,
/// evitando acrescentar o domínio duas vezes em conexões RDP, SSH, WinSCP ou Credential Manager.
/// </summary>
public static class UserIdentityFormatter
{
    public static NormalizedUserIdentity Normalize(string? usernameInput, string? domainInput)
    {
        var rawUser = (usernameInput ?? string.Empty).Trim();
        var rawDomain = (domainInput ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(rawUser))
        {
            return new NormalizedUserIdentity(
                string.Empty,
                string.Empty,
                rawDomain,
                false,
                string.Empty,
                string.Empty);
        }

        // Caso 1: Usuário já contém barra invertida (ex: DOMINIO\usuario ou .\usuario)
        var backslashIndex = rawUser.IndexOf('\\');
        if (backslashIndex >= 0)
        {
            var embeddedDomain = rawUser[..backslashIndex].Trim();
            var cleanUser = rawUser[(backslashIndex + 1)..].Trim();

            // Se o usuário tiver digitado DOMINIO\DOMINIO\usuario acidentalmente, limpa duplicações
            while (cleanUser.Contains('\\'))
            {
                var nextSlash = cleanUser.IndexOf('\\');
                var nextPrefix = cleanUser[..nextSlash].Trim();
                if (string.Equals(nextPrefix, embeddedDomain, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nextPrefix, rawDomain, StringComparison.OrdinalIgnoreCase))
                {
                    cleanUser = cleanUser[(nextSlash + 1)..].Trim();
                }
                else
                {
                    break;
                }
            }

            var effectiveDomain = !string.IsNullOrEmpty(embeddedDomain) ? embeddedDomain : rawDomain;
            var fullLogon = !string.IsNullOrEmpty(effectiveDomain) && !string.IsNullOrEmpty(cleanUser)
                ? $"{effectiveDomain}\\{cleanUser}"
                : cleanUser;

            return new NormalizedUserIdentity(
                rawUser,
                cleanUser,
                effectiveDomain,
                false,
                fullLogon,
                cleanUser);
        }

        // Caso 2: Usuário em formato UPN (usuario@dominio.com)
        var atIndex = rawUser.IndexOf('@');
        if (atIndex > 0 && atIndex < rawUser.Length - 1)
        {
            var userPart = rawUser[..atIndex].Trim();
            var upnDomain = rawUser[(atIndex + 1)..].Trim();

            // Não prefixa o domínio separado quando já está em formato UPN usuario@dominio
            return new NormalizedUserIdentity(
                rawUser,
                userPart,
                !string.IsNullOrEmpty(rawDomain) ? rawDomain : upnDomain,
                true,
                rawUser,
                rawUser);
        }

        // Caso 3: Usuário simples + domínio separado opcional
        if (!string.IsNullOrEmpty(rawDomain))
        {
            // Evita duplicar caso o domínio tenha barra no final
            var cleanDomain = rawDomain.TrimEnd('\\');
            var fullLogon = $"{cleanDomain}\\{rawUser}";
            return new NormalizedUserIdentity(
                rawUser,
                rawUser,
                cleanDomain,
                false,
                fullLogon,
                rawUser);
        }

        // Caso 4: Usuário local sem domínio
        return new NormalizedUserIdentity(
            rawUser,
            rawUser,
            string.Empty,
            false,
            rawUser,
            rawUser);
    }

    public static string FormatDisplay(string? username, string? domain)
    {
        var norm = Normalize(username, domain);
        return norm.EffectiveDownLevelLogonName;
    }
}
