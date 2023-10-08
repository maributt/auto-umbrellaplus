using System.Text.RegularExpressions;

namespace Auto_UmbrellaPlus
{
    internal partial class RegexPatterns
    {

        [GeneratedRegex("^Dieser Schirm wird bei Regen automatisch verwendet: (.*?)\\.$")]
        internal static partial Regex DeAutoUmbrellaLogMessage();

        [GeneratedRegex("(^.*?) selected as auto-umbrella\\.$")]
        internal static partial Regex EnAutoUmbrellaLogMessage();

        [GeneratedRegex("^Vous avez enregistré (.*?) comme accessoire à utiliser automatiquement par temps de pluie\\.$")]
        internal static partial Regex FrAutoUmbrellaLogMessage();

        [GeneratedRegex("\\d{1,3}")]
        internal static partial Regex IdRegex();

        [GeneratedRegex("(^.*?)を雨天時に自動使用するパラソルとして登録しました。$")]
        internal static partial Regex JpAutoUmbrellaLogMessage();

        [GeneratedRegex("\"(.*?)\"")]
        internal static partial Regex UmbrellaNameMatch();
    }
}
