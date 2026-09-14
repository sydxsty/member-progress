using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudentProgress;

public sealed partial class AppService
{
    void ReorderStudents(JsonObject payload)
    {
        if(payload["ids"] is not JsonArray list||list.Any(x=>x is not JsonValue value||!value.TryGetValue<string>(out _)))
            throw new InvalidOperationException("成员顺序无效，请刷新后重试。");
        var ids=list.Select(x=>x!.GetValue<string>()).ToArray();
        var students=Rows("students").ToArray();
        var byId=students.ToDictionary(x=>S(x,"id"),StringComparer.Ordinal);
        if(ids.Length!=students.Length||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length||ids.Any(id=>!byId.ContainsKey(id)))
            throw new InvalidOperationException("成员列表已变化，请刷新后重新排序。");
        var ordered=ids.Select(id=>byId[id]).ToArray();
        Arr("students").Clear();
        foreach(var student in ordered)Arr("students").Add(student);
    }

    // Host maintenance is independent from whether Windows notifications are enabled.
    public bool RefreshStudentGrades()
    {
        bool changed;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var year=AcademicYear(DateTimeOffset.Now);
            if(!Rows("students").Any(s=>I(s,"gradeAcademicYear")<1900||I(s,"gradeAcademicYear")>9999||I(s,"gradeAcademicYear")<year))return false;
            var previous=(JsonObject)state.DeepClone();
            try { changed=AdvanceStudentGrades(state,DateTimeOffset.Now);if(changed)Save(); }
            catch { state=previous;throw; }
        }
        if(changed)Notify();
        return changed;
    }

    static int AcademicYear(DateTimeOffset now)
    {
        var local=TimeZoneInfo.ConvertTime(now,TimeZoneInfo.Local);
        return local.Month>=9?local.Year:local.Year-1;
    }

    static bool AdvanceStudentGrades(JsonObject data,DateTimeOffset now)
    {
        var year=AcademicYear(now);var changed=false;
        foreach(var student in ((JsonArray)data["students"]!).OfType<JsonObject>())
        {
            var previous=I(student,"gradeAcademicYear");
            // Existing manual grades become this year's baseline, with no guessed enrollment year.
            if(previous<1900||previous>9999)
            {
                student["gradeAcademicYear"]=year;changed=true;continue;
            }
            if(previous>=year)continue;
            student["grade"]=AdvanceGrade(S(student,"grade"),year-previous);
            student["gradeAcademicYear"]=year;changed=true;
        }
        return changed;
    }

    internal static string AdvanceGrade(string grade,int years)
    {
        if(years<=0)return grade;
        var match=Regex.Match(grade,@"^(博|研|大|硕)([0-9]+|[零〇一二三四五六七八九十百千]+)(年级)?$",RegexOptions.CultureInvariant);
        if(!match.Success)return grade;
        var text=match.Groups[2].Value;var arabic=text.All(char.IsAsciiDigit);
        int value;
        if(arabic)
        {
            if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out value))return grade;
        }
        else
        {
            // Accept canonical Chinese numerals (plus 〇 for zero), not arbitrary numeral strings.
            value=-1;
            for(var i=0;i<=9999;i++)if(ChineseGradeNumber(i)==text||(i==0&&text=="〇")){value=i;break;}
            if(value<0)return grade;
        }
        if(value<0||value>9999-years)return grade;
        var number=arabic?(value+years).ToString(CultureInfo.InvariantCulture):ChineseGradeNumber(value+years);
        return match.Groups[1].Value+number+match.Groups[3].Value;
    }

    static string ChineseGradeNumber(int value)
    {
        const string digits="零一二三四五六七八九";
        if(value<10)return digits[value].ToString();
        var result="";var zero=false;
        foreach(var (unit,label) in new[]{(1000,"千"),(100,"百"),(10,"十"),(1,"")})
        {
            var digit=value/unit;value%=unit;
            if(digit==0){if(result.Length>0&&value>0)zero=true;continue;}
            if(zero){result+="零";zero=false;}
            if(!(digit==1&&unit==10&&result.Length==0))result+=digits[digit];
            result+=label;
        }
        return result;
    }
}
