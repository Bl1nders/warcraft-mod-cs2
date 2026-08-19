using System.Text;

namespace WarcraftMod.Core;

/// <summary>
/// Перевод набранного в русской раскладке в то, что игрок хотел напечатать.
///
/// Речь не о транслите, а о клавишах: «цс» и «wc» — это одни и те же две кнопки,
/// просто раскладка не та. Человек в бою не смотрит на язык ввода, и команда,
/// отвергнутая из-за этого, выглядит как поломка мода, а не как своя ошибка.
/// </summary>
public static class KeyboardLayout
{
    // Ряды клавиатуры друг под другом: буква и то, что на той же клавише латиницей.
    private const string Cyrillic = "йцукенгшщзхъфывапролджэячсмитьбю.ё";
    private const string Latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`";

    /// <summary>Есть ли в строке кириллица — по ней и решаем, стоит ли вообще переводить.</summary>
    public static bool HasCyrillic(string text)
    {
        foreach (var symbol in text)
        {
            var lower = char.ToLowerInvariant(symbol);
            if (lower is 'ё' || (lower >= 'а' && lower <= 'я')) return true;
        }

        return false;
    }

    /// <summary>Перевести по клавишам. Незнакомые символы остаются как есть.</summary>
    public static string ToLatin(string text)
    {
        var result = new StringBuilder(text.Length);

        foreach (var symbol in text)
        {
            var index = Cyrillic.IndexOf(char.ToLowerInvariant(symbol));
            result.Append(index >= 0 ? Latin[index] : symbol);
        }

        return result.ToString();
    }
}
