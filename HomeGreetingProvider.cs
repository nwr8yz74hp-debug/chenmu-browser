using System;
using System.Globalization;

namespace ChenmuBrowser
{
    internal static class HomeGreetingProvider
    {
        private static readonly string[] MorningGreetings =
        {
            "早上好，愿你从容开启新一天",
            "早上好，今天也有值得期待的事",
            "早上好，愿好心情伴你出发",
            "早上好，慢慢来，也会有收获",
            "早上好，愿今天的努力开花结果",
            "早上好，先照顾好自己再出发",
            "早上好，给新的一天一个微笑",
            "早上好，愿你心中有光脚下有路",
            "早上好，新的开始总有新的可能",
            "早上好，愿你忙而有序，心有余闲",
            "早上好，认真生活的人自带光芒",
            "早上好，今天也请相信自己的节奏",
            "早上好，愿小小进步汇成大大的改变",
            "早上好，千里之行，始于足下。——《老子》",
            "早上好，天行健，君子以自强不息。——《周易》",
            "早上好，愿你发现世界，也发现惊喜",
            "早上好，愿今天遇见的人和事都温柔",
            "早上好，带着期待开始今天的旅程"
        };

        private static readonly string[] AfternoonGreetings =
        {
            "下午好，愿你此刻心情明朗",
            "下午好，忙里也别忘了喝口水",
            "下午好，愿手边的事渐渐有进展",
            "下午好，给自己一点喘息的时间",
            "下午好，一点点向前也是好成绩",
            "下午好，愿灵感和好消息都来敲门",
            "下午好，专注当下，剩下的交给时间",
            "下午好，愿你的认真都被温柔以待",
            "下午好，暂歇片刻，再继续闪闪发光",
            "下午好，愿平凡日子也有小小欢喜",
            "下午好，长风破浪会有时。——李白",
            "下午好，不积跬步，无以至千里。——《荀子》",
            "下午好，愿困难有解，努力有回响",
            "下午好，保持好奇，世界还有新答案",
            "下午好，今天的你已经做得很好了",
            "下午好，愿每一步都走得踏实自在",
            "下午好，心怀热爱，日子就有光彩",
            "下午好，愿接下来的时光顺心如意"
        };

        private static readonly string[] EveningGreetings =
        {
            "晚上好，愿你带着收获轻松归来",
            "晚上好，忙碌一天，记得好好休息",
            "晚上好，愿今晚有一餐热饭和好心情",
            "晚上好，把辛苦放一放，给自己一点温柔",
            "晚上好，今天的努力值得一个肯定",
            "晚上好，愿温暖灯火照亮回家的路",
            "晚上好，愿你卸下疲惫，心里安稳",
            "晚上好，生活的美好常藏在小事里",
            "晚上好，感谢今天认真生活的自己",
            "晚上好，愿烦恼渐远，安心渐近",
            "晚上好，纸上得来终觉浅。——陆游",
            "晚上好，会当凌绝顶，一览众山小。——杜甫",
            "晚上好，愿你与喜欢的人分享好时光",
            "晚上好，慢一点也没关系，平安就好",
            "晚上好，愿明天的你比今天更轻松",
            "晚上好，今天告一段落，明天继续美好",
            "晚上好，记得给自己留一点喜欢的时间",
            "晚上好，愿宁静夜色带来踏实与满足"
        };

        private static readonly string[] LateNightGreetings =
        {
            "夜深了，愿你安然，也有好梦",
            "夜深了，辛苦的你该好好休息了",
            "夜深了，愿烦心事都留在今天",
            "夜深了，给自己一个安心入睡的理由",
            "夜深了，愿明天醒来又是新的晴朗",
            "夜深了，愿你心安入梦，醒来有光",
            "夜深了，先放下忙碌，好好照顾自己",
            "夜深了，愿所有牵挂都化作温柔晚安",
            "夜深了，今天已足够努力，晚安",
            "夜深了，愿月色温柔，梦境轻盈",
            "夜深了，愿你被生活温柔拥抱",
            "夜深了，休息也是向前的一种力量",
            "夜深了，山重水复，终有柳暗花明。——陆游",
            "夜深了，愿心事有归处，疲惫有安放",
            "夜深了，愿今晚睡得踏实，明天顺利",
            "夜深了，世界很大，明天再继续发现",
            "夜深了，放慢呼吸，让自己安心下来",
            "夜深了，愿你梦里有星光，醒来有希望"
        };

        private static readonly string[] NewYearGreetings =
        {
            "元旦快乐，愿新一年平安顺遂、常有惊喜",
            "新年好，愿每个新的开始都充满希望"
        };

        private static readonly string[] LunarNewYearGreetings =
        {
            "新春快乐，愿阖家安康、万事顺意",
            "新年快乐，愿新岁有盼、日子有光"
        };

        private static readonly string[] LunarNewYearEveGreetings =
        {
            "除夕快乐，愿团圆饭暖胃也暖心",
            "除夕安康，愿团圆相伴、灯火可亲"
        };

        private static readonly string[] LanternFestivalGreetings =
        {
            "元宵快乐，愿月圆人圆，所愿皆甜",
            "元宵安康，愿团圆喜乐，心中有光"
        };

        private static readonly string[] DragonBoatGreetings =
        {
            "端午安康，愿清风相伴，平安顺遂",
            "端午安康，愿你身心舒畅，万事皆安"
        };

        private static readonly string[] MidAutumnGreetings =
        {
            "中秋快乐，愿月圆人安，团聚常在",
            "中秋安康，愿生活如月色般温柔明亮"
        };

        private static readonly string[] ValentinesDayGreetings =
        {
            "情人节快乐，愿你被爱，也自在地爱自己",
            "愿今天有心动，也有温柔相伴"
        };

        private static readonly string[] WomensDayGreetings =
        {
            "妇女节快乐，愿你自信闪耀，活成喜欢的模样",
            "愿每一份热爱与努力都被看见，节日快乐"
        };

        private static readonly string[] LaborDayGreetings =
        {
            "劳动节快乐，愿辛勤有回报，休息也安心",
            "劳动节到了，放松身心，享受属于自己的时光"
        };

        private static readonly string[] MothersDayGreetings =
        {
            "母亲节快乐，愿温柔与爱常伴每一位妈妈",
            "母亲节，记得向重要的人说声谢谢与爱"
        };

        private static readonly string[] FathersDayGreetings =
        {
            "父亲节快乐，愿每一份默默守护都被珍惜",
            "父亲节到了，愿关心与陪伴温暖彼此"
        };

        private static readonly string[] TeachersDayGreetings =
        {
            "教师节快乐，感谢每一份耐心指引与点亮",
            "教师节，愿所有老师桃李芬芳、平安喜乐"
        };

        private static readonly string[] DoubleTenGreetings =
        {
            "双十节快乐，愿平安喜乐常伴你我",
            "愿今天有欢庆，也有对生活的美好期待"
        };

        private static readonly string[] ChristmasEveGreetings =
        {
            "平安夜快乐，愿平安喜乐，温暖相伴",
            "平安夜，愿你收到祝福，也记得善待自己"
        };

        private static readonly string[] ChristmasGreetings =
        {
            "圣诞快乐，愿温暖、惊喜与好心情相伴",
            "圣诞快乐，愿你的冬日有光，也有拥抱"
        };

        private static readonly string[] YearEndGreetings =
        {
            "岁末安好，感谢努力生活的自己",
            "一年将尽，愿遗憾释怀，期待如约而至"
        };

        internal static int DailyGreetingCount
        {
            get
            {
                return MorningGreetings.Length + AfternoonGreetings.Length +
                    EveningGreetings.Length + LateNightGreetings.Length;
            }
        }

        internal static string GetGreeting(DateTime now)
        {
            string[] holidayGreetings = GetHolidayGreetings(now);
            if (holidayGreetings != null)
                return ChooseForDate(holidayGreetings, now, 97);

            string[] greetings = GetDailyGreetings(now.Hour);
            return ChooseForDate(greetings, now, GetPeriodIndex(now.Hour) * 31 + 7);
        }

        private static string[] GetHolidayGreetings(DateTime date)
        {
            if (date.Month == 1 && date.Day == 1)
                return NewYearGreetings;
            if (date.Month == 2 && date.Day == 14)
                return ValentinesDayGreetings;
            if (date.Month == 3 && date.Day == 8)
                return WomensDayGreetings;
            if (date.Month == 5 && date.Day == 1)
                return LaborDayGreetings;
            if (date.Month == 5 && IsNthWeekdayOfMonth(date, DayOfWeek.Sunday, 2))
                return MothersDayGreetings;
            if (date.Month == 6 && IsNthWeekdayOfMonth(date, DayOfWeek.Sunday, 3))
                return FathersDayGreetings;
            if (date.Month == 9 && date.Day == 28)
                return TeachersDayGreetings;
            if (date.Month == 10 && date.Day == 10)
                return DoubleTenGreetings;
            if (date.Month == 12 && date.Day == 24)
                return ChristmasEveGreetings;
            if (date.Month == 12 && date.Day == 25)
                return ChristmasGreetings;
            if (date.Month == 12 && date.Day == 31)
                return YearEndGreetings;

            int lunarMonth;
            int lunarDay;
            int lunarDaysInMonth;
            if (!TryGetLunarDate(date, out lunarMonth, out lunarDay, out lunarDaysInMonth))
                return null;
            if (lunarMonth == 1 && lunarDay == 1)
                return LunarNewYearGreetings;
            if (lunarMonth == 1 && lunarDay == 15)
                return LanternFestivalGreetings;
            if (lunarMonth == 5 && lunarDay == 5)
                return DragonBoatGreetings;
            if (lunarMonth == 8 && lunarDay == 15)
                return MidAutumnGreetings;
            if (lunarMonth == 12 && lunarDay == lunarDaysInMonth)
                return LunarNewYearEveGreetings;
            return null;
        }

        private static bool TryGetLunarDate(DateTime date, out int month, out int day, out int daysInMonth)
        {
            month = 0;
            day = 0;
            daysInMonth = 0;
            try
            {
                var calendar = new ChineseLunisolarCalendar();
                int year = calendar.GetYear(date);
                month = calendar.GetMonth(date);
                day = calendar.GetDayOfMonth(date);
                daysInMonth = calendar.GetDaysInMonth(year, month);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static bool IsNthWeekdayOfMonth(DateTime date, DayOfWeek weekday, int occurrence)
        {
            return date.DayOfWeek == weekday && ((date.Day - 1) / 7) + 1 == occurrence;
        }

        private static string[] GetDailyGreetings(int hour)
        {
            if (hour >= 5 && hour < 12)
                return MorningGreetings;
            if (hour >= 12 && hour < 18)
                return AfternoonGreetings;
            if (hour >= 18 && hour < 23)
                return EveningGreetings;
            return LateNightGreetings;
        }

        private static int GetPeriodIndex(int hour)
        {
            if (hour >= 5 && hour < 12)
                return 0;
            if (hour >= 12 && hour < 18)
                return 1;
            if (hour >= 18 && hour < 23)
                return 2;
            return 3;
        }

        private static string ChooseForDate(string[] greetings, DateTime date, int salt)
        {
            int index = (int)(((long)date.Year * 367 + date.DayOfYear + salt) % greetings.Length);
            return greetings[index];
        }
    }
}
