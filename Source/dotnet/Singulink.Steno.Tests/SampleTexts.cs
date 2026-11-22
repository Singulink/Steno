namespace Singulink.Steno.Tests;

/// <summary>
/// Small hand-picked sample texts shared by the unit tests and the benchmark/size-comparison tool.
/// </summary>
public static class SampleTexts
{
    public static IReadOnlyList<(string Name, string Text)> All { get; } = [
        ("Chinese",
            "阳光明媚，微风轻拂，公园里充满了欢声笑语。孩子们在草地上奔跑，老人们悠闲地坐在长椅上聊天。不远处，湖面上倒映着蓝天白云，几只鸭子自在地游来游去。\r\n" +
            "小吃摊上传来诱人的香味，吸引着路过的行人。\r\n" +
            "整个公园洋溢着温馨和谐的气氛，让人流连忘返。"),

        ("Japanese",
            "朝の陽射しがやわらかく街を包み、公園には散歩を楽しむ人々の姿があった。\r\n" +
            "子どもたちは元気よく遊び回り、ベンチでは年配の方々が穏やかに会話を交わしている。\r\n" +
            "近くのカフェからはコーヒーの香ばしい香りが漂い、道行く人々の足を止めさせる。\r\n" +
            "心地よい風が吹き抜ける中、日常の小さな幸せを感じられるひとときだった。"),

        ("Korean",
            "아침 햇살이 부드럽게 비추며 공원에는 산책을 즐기는 사람들이 보였다. 아이들은 신나게 뛰어놀고, 벤치에 앉은 어르신들은 담소를 나누고 있었다.\r\n" +
            "가까운 카페에서는 향긋한 커피 향이 퍼져 나와 지나가는 사람들의 발길을 멈추게 했다.\r\n" +
            "상쾌한 바람이 불어오는 가운데, 일상의 작은 행복을 느낄 수 있는 순간이었다."),

        ("Indian",
            "सुबह की ताजगी के साथ सूरज की किरणें धीरे-धीरे धरती पर फैल रही थीं। पार्क में लोग अपनी सुबह की सैर पर निकले थे,\r\n" +
            "कुछ लोग दौड़ रहे थे तो कुछ शांतिपूर्वक योग कर रहे थे। बच्चे झूलों पर खेल रहे थे और हर जगह खुशहाली का माहौल था। दूर से चायवाले की आवाज सुनाई दे रही थी, जो अपने ठेले के पास चाय बना रहा था। हवा में ताजगी और ऊर्जा का एहसास हो रहा था।"),

        ("German",
            "Der Frühling war endlich gekommen, und die Bäume begannen, ihre Blätter in kräftigem Grün zu entfalten.\r\n" +
            "Auf der Straße herrschte reges Treiben, Menschen gingen einkaufen oder genossen einfach die frische Luft.\r\n" +
            "Kinder spielten auf dem Spielplatz, während ältere Paare gemütlich durch den Park spazierten.\r\n" +
            "In der Ferne hörte man das Zwitschern der Vögel und das leise Rauschen eines Baches.\r\n" +
            "Die warme Sonne tauchte alles in ein angenehmes Licht, und es war ein perfekter Tag, um draußen zu sein."),

        ("Greek",
            "Ο ήλιος έλαμπε δυνατά στον γαλανό ουρανό, καθώς ο κόσμος περπατούσε αργά στους λιθόστρωτους δρόμους της πόλης.\r\n" +
            "Στις καφετέριες, οι άνθρωποι απολάμβαναν τον ελληνικό τους καφέ, συζητώντας ζωηρά.\r\n" +
            "Η μυρωδιά από φρεσκοψημένο ψωμί ξεχύνονταν από τους φούρνους, ενώ τα παιδιά έπαιζαν χαρούμενα στην πλατεία.\r\n" +
            "Ήταν μια όμορφη, ζεστή μέρα γεμάτη ζωή και ενέργεια."),

        ("Devanagari",
            "सुबह की सुनहरी धूप चारों ओर फैल रही थी और हल्की ठंडी हवा माहौल को और भी सुहावना बना रही थी। सड़क के किनारे पेड़ों की छाँव में लोग चाय की चुस्कियाँ लेते हुए बातचीत कर रहे थे। बच्चे पार्क में खेल रहे थे, उनकी हँसी से माहौल जीवंत हो गया था। पास की दुकान से ताज़े पकवानों की खुशबू आ रही थी,\r\n" +
            "जो राहगीरों को अपनी ओर आकर्षित कर रही थी। यह एक आम लेकिन खुशनुमा दिन की शुरुआत थी।"),

        ("French",
            "Le soleil brillait haut dans le ciel, répandant une douce chaleur sur la place animée du village.\r\n" +
            "Les enfants couraient en riant autour de la fontaine tandis que les marchands installaient leurs étals colorés.\r\n" +
            "L’odeur du pain frais sortant de la boulangerie flottait dans l’air, attirant les passants gourmands.\r\n" +
            "Non loin de là, un musicien jouait un air entraînant à la guitare, ajoutant une touche de magie à cette matinée paisible."),

        ("Emojis",
            "🌞🌿🐦🎶☕🏃‍♂️🎈\r\n" +
            "🐶🐾🍕🍜🍣🚶‍♀️🚶‍♂️✈️☁️🌆💖🎉🎶🌺🌊🍉🍹🔥🏖️🌙✨🚀💫🎨🎭🎸🎮🍩\r\n" +
            "🎯🎲🎡🎠🎢🏆📸🎥🎶📚✏️🖊️📝💡🔮💎🎁💰💳📅⏰🔔🎵🎶🎼🎤🎧🎷🎺🎹🎬🎭🎨🖼️🏞️🌅🌄🌇🌃🌌🌠🌈☀️🌤️⛅🌥️🌦️🌧️⛈️🌩️🌨️❄️💨\r\n" +
            "🌪️🌫️🔥💧🌊"),

        ("Mixed languages",
            "阳Straße光 στον γαλανό明媚，微风répandant une douce轻拂，公园里充στον रही थी औरγαλανό满了欢声笑语 の香🌤️ばしい💳\r\n" +
            "📅香りが漂い सूरज की कि🌌रणें धीरे-धी🌇🌃रेξεχύνονταν από τους 🍉! This is a bunch of english, this is not: पर खेल रहे थे और."),
    ];

    public static IEnumerable<object[]> TestData => All.Select(s => new object[] { s.Name, s.Text });
}
