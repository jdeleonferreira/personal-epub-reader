// Atril catalog: reads an EPUB's metadata and text to
//   1) recognise a book that is already in the library (duplicates), using several signals, and
//   2) sort it by knowledge area and topic.
// Everything runs on this computer: no book content leaves it.
//
// Categories: two levels, area → topic. The areas follow the top level of Thema, the international book
// subject standard (free to use, used by publishers and libraries), merged into 20 groups that make sense
// on a personal shelf. Each book is placed in this order:
//   1. Subject codes in the EPUB (BISAC like FIC028000 or Thema like FL): exact mapping.
//   2. Subject words in the EPUB ("Science Fiction", "Historia de España"...), in English or Spanish.
//   3. The book itself: title, description and the first ~60,000 characters, scored against keyword lists
//      for every topic (English and Spanish), plus how much dialogue there is (novels have a lot).
//   4. What the user picks by hand always wins.
(() => {
  const CATALOG_VERSION = 1;

  /* ---------- Text helpers ---------- */
  const fold = s => String(s || "").normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  const words = s => fold(s).replace(/[^a-z0-9]+/g, " ").trim();
  const ARTICLES = /^(the|a|an|el|la|los|las|un|una|lo|le|les|il|der|die|das)\s+/;
  function titleKeys(t) {
    let full = fold(t).replace(/\[[^\]]*\]|\([^)]*\)/g, " "); // "(Spanish Edition)", "[Illustrated]"
    const main = words(full.split(/\s*[:;]\s+|\s+[-–—]\s+/)[0]).replace(ARTICLES, "");
    full = words(full).replace(ARTICLES, "");
    return { full, main: main || full };
  }
  // "Tolkien, J. R. R." and "J.R.R. Tolkien" give the same key
  const authorKey = a => words(a).split(" ").filter(Boolean).sort().join(" ");
  function authorsMatch(a, b) {
    if (!a.length || !b.length) return !a.length && !b.length;
    if (a.some(x => b.includes(x))) return true;
    const ta = new Set(a.flatMap(x => x.split(" ")).filter(w => w.length >= 4));
    return b.some(x => x.split(" ").some(w => ta.has(w)));
  }

  /* ---------- ISBN ---------- */
  function isbn13(raw) {
    const s = String(raw || "").toUpperCase().replace(/^URN:ISBN:|^ISBN[:\s]*/i, "").replace(/[^0-9X]/g, "");
    if (s.length === 10 && /^\d{9}[\dX]$/.test(s)) {
      let sum = 0; for (let i = 0; i < 10; i++) sum += (10 - i) * (s[i] === "X" ? 10 : +s[i]);
      if (sum % 11) return null;
      const body = "978" + s.slice(0, 9);
      let c = 0; for (let i = 0; i < 12; i++) c += (i % 2 ? 3 : 1) * +body[i];
      return body + ((10 - c % 10) % 10);
    }
    if (s.length === 13 && /^97[89]\d{10}$/.test(s)) {
      let c = 0; for (let i = 0; i < 13; i++) c += (i % 2 ? 3 : 1) * +s[i];
      return c % 10 ? null : s;
    }
    return null;
  }

  /* ---------- Reading the EPUB ---------- */
  const DC = "http://purl.org/dc/elements/1.1/";
  const xml = s => new DOMParser().parseFromString(s, "application/xml");
  function resolve(base, href) {
    const out = [];
    for (const p of (base + decodeURIComponent(href.split("#")[0])).split("/")) {
      if (p === "..") out.pop(); else if (p && p !== ".") out.push(p);
    }
    return out.join("/");
  }
  function zipFile(zip, path) {
    return zip.file(path) || zip.file(new RegExp("^" + path.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + "$", "i"))[0] || null;
  }

  /** Metadata, size and text sample of an EPUB. */
  async function analyze(buf) {
    const zip = await JSZip.loadAsync(buf);
    const container = xml(await zipFile(zip, "META-INF/container.xml").async("string"));
    const opfPath = container.getElementsByTagName("rootfile")[0].getAttribute("full-path");
    const opf = xml(await zipFile(zip, opfPath).async("string"));
    const base = opfPath.includes("/") ? opfPath.replace(/[^/]*$/, "") : "";
    const dc = n => [...opf.getElementsByTagNameNS(DC, n)];
    const metas = [...opf.getElementsByTagName("meta")];
    const refined = (el, prop) => {
      const id = el.getAttribute("id"); if (!id) return "";
      const m = metas.find(m => m.getAttribute("refines") === "#" + id && m.getAttribute("property") === prop);
      return m ? m.textContent.trim() : "";
    };

    const title = (dc("title")[0] || {}).textContent?.trim() || "";
    const authors = dc("creator").map(e => e.textContent.trim()).filter(Boolean);
    const description = dc("description").map(e => e.textContent).join(" ").replace(/<[^>]+>/g, " ").replace(/\s+/g, " ").trim().slice(0, 3000);
    const language = (dc("language")[0] || {}).textContent?.trim() || "";
    const subjects = dc("subject").map(e => ({
      text: e.textContent.trim(),
      scheme: (e.getAttribute("opf:authority") || e.getAttribute("authority") || e.getAttribute("opf:scheme") || refined(e, "authority") || "").toUpperCase(),
      code: refined(e, "term")
    })).filter(s => s.text || s.code);
    // calibre stores its tags as subjects too; some tools use <meta name="subject">
    for (const m of metas) if (/^(subject|keywords)$/i.test(m.getAttribute("name") || "")) for (const t of (m.getAttribute("content") || "").split(/[,;]/)) if (t.trim()) subjects.push({ text: t.trim(), scheme: "", code: "" });

    const isbns = new Set(), ids = new Set();
    for (const e of dc("identifier")) {
      const v = e.textContent.trim();
      const scheme = (e.getAttribute("opf:scheme") || e.getAttribute("scheme") || refined(e, "identifier-type") || "").toUpperCase();
      const i = isbn13(v);
      if (i && (scheme.includes("ISBN") || /isbn/i.test(v) || /^[\d\s-]{10,17}X?$/i.test(v))) { isbns.add(i); continue; }
      const id = fold(v).replace(/^urn:(uuid|uri|doi|isbn):/, "").replace(/\s+/g, "");
      if (id.length >= 8 && !/^(bookid|uuid_id|pub-id|id|calibre)$/.test(id)) ids.add(id);
    }

    // Book text, in reading order (spine)
    const manifest = new Map([...opf.getElementsByTagName("item")].map(i => [i.getAttribute("id"), i]));
    const spine = [...opf.getElementsByTagName("itemref")].map(r => manifest.get(r.getAttribute("idref"))).filter(Boolean)
      .filter(i => /x?html|xml/i.test(i.getAttribute("media-type") || "html"));
    let chars = 0, sample = "", paragraphs = 0, dialogue = 0;
    for (const item of spine.slice(0, 400)) {
      const f = zipFile(zip, resolve(base, item.getAttribute("href") || "")); if (!f) continue;
      const doc = new DOMParser().parseFromString(await f.async("string"), "text/html");
      const text = (doc.body ? doc.body.textContent : "").replace(/\s+/g, " ").trim();
      chars += text.length;
      if (sample.length < 60000) {
        sample += " " + text.slice(0, 60000 - sample.length);
        for (const p of doc.querySelectorAll("p")) {
          const t = p.textContent.trim(); if (t.length < 2) continue;
          paragraphs++; if (/^[—–\-"“«‘']/.test(t)) dialogue++;
        }
      }
    }
    // ISBN printed on the copyright page (only near the start, so books listed at the end don't count)
    const printed = new Set();
    for (const m of sample.slice(0, 20000).matchAll(/ISBN(?:-1[03])?[:\s]*((?:97[89][\s-]?)?(?:\d[\s-]?){9}[\dX])/gi)) { const i = isbn13(m[1]); if (i) printed.add(i); }

    return {
      v: CATALOG_VERSION, title, authors, description, language, subjects,
      isbns: [...isbns], printedIsbns: [...printed].filter(i => !isbns.has(i)), ids: [...ids],
      spine: spine.length, chars,
      pages: Math.max(1, Math.round(chars / 1800)), // a standard page is about 1,800 characters
      dialogue: paragraphs ? dialogue / paragraphs : 0,
      sample
    };
  }

  /* ---------- Duplicates ---------- */
  /** What gets stored with each book to compare it with new ones (no text). */
  function signature(a, size) {
    const t = titleKeys(a.title);
    return {
      v: CATALOG_VERSION, size, title: t.full, mainTitle: t.main, authors: a.authors.map(authorKey).filter(Boolean),
      isbns: a.isbns, printedIsbns: a.printedIsbns, ids: a.ids, pages: a.pages, spine: a.spine
    };
  }
  const near = (a, b) => a && b && Math.abs(a - b) <= Math.max(2, 0.03 * Math.max(a, b));
  /**
   * Returns why two books are the same, or null.
   * Signals, strongest first: identical file → same ISBN → same publisher identifier and title →
   * same title and author with the same length (pages and sections).
   */
  function sameBook(n, o) {
    if (!n || !o) return null;
    const isbn = n.isbns.find(i => o.isbns.includes(i));
    if (isbn) return `same ISBN ${isbn}`;
    const sameMain = n.mainTitle && n.mainTitle === o.mainTitle;
    const printed = [...n.isbns, ...n.printedIsbns].find(i => [...o.isbns, ...o.printedIsbns].includes(i));
    if (printed && sameMain) return `same ISBN ${printed}`;
    if (sameMain && n.ids.some(i => o.ids.includes(i))) return "same book identifier";
    const sameTitle = n.title && n.title === o.title;
    if ((sameTitle || sameMain) && authorsMatch(n.authors, o.authors) && near(n.pages, o.pages) && (sameTitle || near(n.spine, o.spine)))
      return `same title, author and length, about ${n.pages} pages`;
    return null;
  }

  /* ---------- Categories ---------- */
  // Areas follow the top level of Thema. bisac: 3-letter BISAC prefixes. thema: Thema code prefixes.
  // kw: words that name the area itself (used on subject words). topics: the second level, each with
  // keywords looked for in the subjects and in the text (accents and case don't matter; English and Spanish).
  const AREAS = [
    { id: "fiction", label: "Fiction & Literature", bisac: ["FIC", "LIT", "POE", "DRA", "LCO", "CGN", "HUM"], thema: ["F", "D", "X"],
      kw: ["fiction", "ficcion", "novel", "novela", "novels", "novelas", "literature", "literatura", "short stories", "cuentos", "relatos", "narrativa"],
      topics: [
        { label: "Science fiction", kw: ["science fiction", "ciencia ficcion", "sci fi", "spaceship", "nave espacial", "starship", "galaxy", "galaxia", "planet", "planeta", "android", "androide", "robot", "alien", "alienigena", "cyberpunk", "space opera", "dystopia", "distopia"] },
        { label: "Fantasy", kw: ["fantasy", "fantasia", "dragon", "dragons", "dragones", "wizard", "mago", "magic", "magia", "sword", "espada", "elf", "elfo", "elves", "elfos", "kingdom", "reino", "sorcerer", "hechicero", "spell", "hechizo", "dwarf", "enano"] },
        { label: "Mystery & crime", kw: ["mystery", "misterio", "detective", "murder", "asesinato", "inspector", "crime", "crimen", "policiaca", "noir", "suspect", "sospechoso", "homicide", "homicidio"] },
        { label: "Thriller & suspense", kw: ["thriller", "suspense", "espionage", "espionaje", "spy", "espia", "conspiracy", "conspiracion", "agent", "agente"] },
        { label: "Romance", kw: ["romance", "romantica", "romantic", "love story", "historia de amor", "kiss", "beso", "besos", "kissed", "beso", "wedding", "boda"] },
        { label: "Horror", kw: ["horror", "terror", "ghost", "fantasma", "vampire", "vampiro", "zombie", "zombi", "demon", "demonio", "haunted", "embrujada"] },
        { label: "Historical fiction", kw: ["historical fiction", "novela historica", "historical novel"] },
        { label: "Poetry", kw: ["poetry", "poesia", "poems", "poemas", "verse", "versos", "sonnet", "soneto"] },
        { label: "Drama & theatre", kw: ["drama", "theatre", "theater", "teatro", "obra de teatro", "act i", "acto primero"] },
        { label: "Comics & graphic novels", kw: ["comics", "comic", "graphic novel", "novela grafica", "manga"] },
        { label: "Humour", kw: ["humor", "humour", "comedy", "comedia", "satire", "satira"] },
        { label: "Literary criticism", kw: ["literary criticism", "critica literaria", "literary theory", "teoria literaria"] }
      ] },
    { id: "children", label: "Children & Young Adult", bisac: ["JUV", "JNF", "YAF", "YAN"], thema: ["Y"],
      kw: ["juvenile", "juvenil", "children", "infantil", "ninos", "kids", "young adult", "teen", "adolescentes"],
      topics: [
        { label: "Children's books", kw: ["children", "infantil", "picture book", "album ilustrado", "fairy tale", "cuento de hadas", "bedtime"] },
        { label: "Young adult", kw: ["young adult", "juvenil", "teen", "adolescente", "high school", "instituto"] }
      ] },
    { id: "biography", label: "Biography & Memoir", bisac: ["BIO"], thema: ["DN"],
      kw: ["biography", "biografia", "biografias", "autobiography", "autobiografia", "memoir", "memoirs", "memorias"],
      topics: [
        { label: "Biography", kw: ["biography", "biografia", "his life", "su vida", "was born in", "nacio en"] },
        { label: "Memoir", kw: ["memoir", "memorias", "autobiography", "autobiografia", "my life", "mi vida", "diary", "diario"] }
      ] },
    { id: "history", label: "History", bisac: ["HIS"], thema: ["N"],
      kw: ["history", "historia", "historical", "historico", "historica"],
      topics: [
        { label: "Ancient history", kw: ["ancient", "antigua", "antiguedad", "rome", "roma", "roman empire", "imperio romano", "greece", "grecia", "egypt", "egipto", "pharaoh", "faraon", "mesopotamia"] },
        { label: "Medieval history", kw: ["medieval", "middle ages", "edad media", "crusade", "cruzada", "feudal", "knights", "caballeros", "byzantine", "bizantino"] },
        { label: "Modern history", kw: ["revolution", "revolucion", "industrial revolution", "renaissance", "renacimiento", "enlightenment", "ilustracion", "colonial", "independence", "independencia", "century", "siglo"] },
        { label: "Military history", kw: ["war", "guerra", "battle", "batalla", "army", "ejercito", "world war", "guerra mundial", "soldiers", "soldados", "military", "militar", "civil war", "guerra civil"] },
        { label: "History of the Americas", kw: ["latin america", "america latina", "latinoamerica", "conquest", "conquista", "aztec", "azteca", "inca", "maya", "spanish empire", "virreinato", "united states", "estados unidos"] }
      ] },
    { id: "philosophy", label: "Philosophy & Religion", bisac: ["PHI", "REL", "BIB", "OCC"], thema: ["Q"],
      kw: ["philosophy", "filosofia", "religion", "religiones", "spirituality", "espiritualidad", "theology", "teologia", "ethics", "etica"],
      topics: [
        { label: "Philosophy", kw: ["philosophy", "filosofia", "philosopher", "filosofo", "metaphysics", "metafisica", "ethics", "etica", "epistemology", "epistemologia", "stoic", "estoico", "plato", "platon", "aristotle", "aristoteles", "kant", "nietzsche", "existence", "existencia", "reason", "razon"] },
        { label: "Religion & spirituality", kw: ["god", "dios", "church", "iglesia", "bible", "biblia", "christ", "cristo", "jesus", "faith", "fe", "prayer", "oracion", "spiritual", "espiritual", "buddhism", "budismo", "islam", "judaism", "judaismo", "theology", "teologia", "meditation", "meditacion"] },
        { label: "Esotericism", kw: ["esoteric", "esoterico", "astrology", "astrologia", "tarot", "occult", "ocultismo", "kabbalah", "cabala", "alchemy", "alquimia"] }
      ] },
    { id: "psychology", label: "Psychology & Personal Growth", bisac: ["PSY", "SEL", "FAM"], thema: ["JM", "V"],
      kw: ["psychology", "psicologia", "self help", "autoayuda", "personal development", "desarrollo personal", "crecimiento personal", "family", "familia", "relationships", "relaciones"],
      topics: [
        { label: "Psychology", kw: ["psychology", "psicologia", "psychologist", "psicologo", "cognitive", "cognitivo", "behavior", "behaviour", "conducta", "emotion", "emocion", "emociones", "anxiety", "ansiedad", "depression", "depresion", "therapy", "terapia", "mind", "mente", "brain", "cerebro", "freud", "jung"] },
        { label: "Self-help", kw: ["self help", "autoayuda", "habits", "habitos", "success", "exito", "motivation", "motivacion", "goals", "metas", "productivity", "productividad", "mindset", "happiness", "felicidad", "confidence", "confianza", "you can", "tu puedes"] },
        { label: "Family & parenting", kw: ["parenting", "crianza", "children", "hijos", "parents", "padres", "baby", "bebe", "family", "familia", "maternity", "maternidad", "paternity", "paternidad"] },
        { label: "Relationships", kw: ["relationship", "relationships", "relacion", "relaciones", "couple", "pareja", "marriage", "matrimonio", "dating", "communication", "comunicacion"] }
      ] },
    { id: "society", label: "Society & Politics", bisac: ["SOC", "POL", "TRU"], thema: ["J"],
      kw: ["social science", "ciencias sociales", "sociology", "sociologia", "politics", "politica", "political science", "ciencia politica", "true crime", "anthropology", "antropologia", "current affairs", "actualidad"],
      topics: [
        { label: "Politics", kw: ["politics", "politica", "political", "democracy", "democracia", "government", "gobierno", "election", "elecciones", "party", "state", "estado", "power", "poder", "president", "presidente", "congress", "congreso", "parliament", "parlamento"] },
        { label: "Sociology & anthropology", kw: ["society", "sociedad", "social", "sociology", "sociologia", "culture", "cultura", "anthropology", "antropologia", "gender", "genero", "inequality", "desigualdad", "class", "clase social", "community", "comunidad"] },
        { label: "Journalism & current affairs", kw: ["journalism", "periodismo", "media", "medios", "news", "noticias", "report", "reportaje", "chronicle", "cronica"] },
        { label: "True crime", kw: ["true crime", "serial killer", "asesino en serie", "investigation", "investigacion", "trial", "juicio", "victim", "victima"] }
      ] },
    { id: "business", label: "Business & Economics", bisac: ["BUS"], thema: ["K"],
      kw: ["business", "negocios", "economics", "economia", "management", "gestion", "finance", "finanzas", "marketing", "empresa"],
      topics: [
        { label: "Economics", kw: ["economy", "economia", "economic", "economico", "market", "mercado", "inflation", "inflacion", "capital", "capitalism", "capitalismo", "gdp", "pib", "trade", "comercio", "growth", "crecimiento"] },
        { label: "Management & leadership", kw: ["management", "gestion", "leadership", "liderazgo", "leader", "lider", "team", "equipo", "manager", "gerente", "organization", "organizacion", "strategy", "estrategia", "company", "empresa"] },
        { label: "Finance & investing", kw: ["finance", "finanzas", "investing", "inversion", "invest", "invertir", "stock", "money", "dinero", "wealth", "riqueza", "bank", "banco", "portfolio", "cartera", "savings", "ahorro"] },
        { label: "Marketing & sales", kw: ["marketing", "sales", "ventas", "brand", "marca", "customer", "cliente", "clientes", "advertising", "publicidad", "selling", "vender"] },
        { label: "Entrepreneurship", kw: ["startup", "entrepreneur", "emprendedor", "emprendimiento", "founder", "fundador", "business plan", "plan de negocio"] }
      ] },
    { id: "law", label: "Law", bisac: ["LAW"], thema: ["L"],
      kw: ["law", "derecho", "legal", "juridico", "jurisprudence", "jurisprudencia"],
      topics: [{ label: "Law", kw: ["law", "derecho", "legal", "court", "tribunal", "judge", "juez", "contract", "contrato", "constitution", "constitucion", "statute", "ley", "leyes", "rights", "derechos", "lawyer", "abogado"] }] },
    { id: "science", label: "Science & Mathematics", bisac: ["SCI", "MAT", "NAT"], thema: ["P", "R"],
      kw: ["science", "ciencia", "ciencias", "mathematics", "matematicas", "nature", "naturaleza", "physics", "fisica", "chemistry", "quimica", "biology", "biologia"],
      topics: [
        { label: "Mathematics", kw: ["mathematics", "matematicas", "theorem", "teorema", "equation", "ecuacion", "algebra", "geometry", "geometria", "calculus", "calculo", "proof", "demostracion", "number theory", "probability", "probabilidad", "statistics", "estadistica"] },
        { label: "Physics", kw: ["physics", "fisica", "quantum", "cuantica", "relativity", "relatividad", "particle", "particula", "energy", "energia", "einstein", "gravity", "gravedad", "atom", "atomo"] },
        { label: "Chemistry", kw: ["chemistry", "quimica", "molecule", "molecula", "chemical", "quimico", "reaction", "reaccion", "element", "elemento"] },
        { label: "Biology & evolution", kw: ["biology", "biologia", "evolution", "evolucion", "species", "especies", "cell", "celula", "gene", "gen", "genes", "dna", "adn", "darwin", "organism", "organismo"] },
        { label: "Astronomy & space", kw: ["astronomy", "astronomia", "universe", "universo", "cosmos", "star", "estrella", "galaxy", "galaxia", "black hole", "agujero negro", "telescope", "telescopio"] },
        { label: "Earth & environment", kw: ["climate", "clima", "environment", "medio ambiente", "ecology", "ecologia", "geology", "geologia", "ocean", "oceano", "planet earth", "climate change", "cambio climatico"] },
        { label: "Nature", kw: ["nature", "naturaleza", "animals", "animales", "plants", "plantas", "birds", "aves", "forest", "bosque", "wildlife", "fauna"] }
      ] },
    { id: "medicine", label: "Medicine & Health", bisac: ["MED", "HEA"], thema: ["M", "VFD", "VFJ", "VFM"],
      kw: ["medicine", "medicina", "health", "salud", "medical", "medico", "fitness", "nutrition", "nutricion"],
      topics: [
        { label: "Medicine", kw: ["medicine", "medicina", "patient", "paciente", "disease", "enfermedad", "doctor", "diagnosis", "diagnostico", "treatment", "tratamiento", "clinical", "clinico", "symptoms", "sintomas", "hospital"] },
        { label: "Nutrition & diet", kw: ["nutrition", "nutricion", "diet", "dieta", "food", "alimentos", "alimentacion", "vitamin", "vitamina", "protein", "proteina", "calories", "calorias"] },
        { label: "Fitness & wellbeing", kw: ["fitness", "exercise", "ejercicio", "training", "entrenamiento", "yoga", "wellness", "bienestar", "sleep", "sueno", "health", "salud"] }
      ] },
    { id: "technology", label: "Technology & Engineering", bisac: ["TEC", "TRA"], thema: ["T"],
      kw: ["technology", "tecnologia", "engineering", "ingenieria", "transportation", "transporte"],
      topics: [
        { label: "Engineering", kw: ["engineering", "ingenieria", "engineer", "ingeniero", "mechanical", "mecanica", "structural", "estructural", "design", "diseno", "materials", "materiales"] },
        { label: "Electronics", kw: ["electronics", "electronica", "circuit", "circuito", "voltage", "voltaje", "arduino", "raspberry", "microcontroller", "microcontrolador"] },
        { label: "Energy", kw: ["energy", "energia", "solar", "renewable", "renovable", "battery", "bateria", "electricity", "electricidad", "nuclear"] },
        { label: "Transport", kw: ["cars", "coches", "automobile", "automovil", "aviation", "aviacion", "aircraft", "avion", "railway", "ferrocarril"] }
      ] },
    { id: "computing", label: "Computing & IT", bisac: ["COM"], thema: ["U"],
      kw: ["computers", "computing", "computacion", "informatica", "programming", "programacion", "software", "computer science"],
      topics: [
        { label: "Programming", kw: ["programming", "programacion", "code", "codigo", "function", "funcion", "class", "clase", "variable", "compiler", "compilador", "python", "java", "javascript", "csharp", "rust", "golang", "typescript", "algorithm", "algoritmo", "developer", "desarrollador", "refactoring"] },
        { label: "Web development", kw: ["html", "css", "web", "frontend", "backend", "react", "angular", "http", "api", "browser", "navegador"] },
        { label: "Data & AI", kw: ["data", "datos", "machine learning", "aprendizaje automatico", "artificial intelligence", "inteligencia artificial", "neural network", "red neuronal", "deep learning", "dataset", "analytics"] },
        { label: "Databases", kw: ["database", "base de datos", "sql", "query", "consulta", "table", "tabla", "postgres", "mysql", "nosql", "index", "indice"] },
        { label: "Networks & security", kw: ["network", "redes", "security", "seguridad", "encryption", "cifrado", "hacker", "firewall", "tcp", "protocol", "protocolo", "attack", "ataque", "vulnerability", "vulnerabilidad"] },
        { label: "Systems & DevOps", kw: ["linux", "windows", "server", "servidor", "cloud", "nube", "docker", "kubernetes", "devops", "deployment", "despliegue", "operating system", "sistema operativo"] }
      ] },
    { id: "arts", label: "Arts & Design", bisac: ["ART", "MUS", "ARC", "DES", "PHO", "PER", "ANT"], thema: ["A"],
      kw: ["art", "arte", "music", "musica", "architecture", "arquitectura", "design", "diseno", "photography", "fotografia", "performing arts", "cine", "film"],
      topics: [
        { label: "Visual arts", kw: ["painting", "pintura", "painter", "pintor", "art", "arte", "sculpture", "escultura", "museum", "museo", "drawing", "dibujo", "artist", "artista"] },
        { label: "Music", kw: ["music", "musica", "song", "cancion", "guitar", "guitarra", "piano", "composer", "compositor", "band", "banda", "album", "chord", "acorde"] },
        { label: "Architecture", kw: ["architecture", "arquitectura", "architect", "arquitecto", "building", "edificio", "urbanism", "urbanismo"] },
        { label: "Photography", kw: ["photography", "fotografia", "camera", "camara", "photo", "foto", "lens", "objetivo"] },
        { label: "Film & performing arts", kw: ["film", "cine", "movie", "pelicula", "director", "actor", "actress", "actriz", "dance", "danza", "screenplay", "guion"] },
        { label: "Design", kw: ["design", "diseno", "typography", "tipografia", "graphic design", "diseno grafico", "ux", "user experience"] }
      ] },
    { id: "language", label: "Language & Writing", bisac: ["LAN", "FOR"], thema: ["C"],
      kw: ["language", "lenguaje", "idioma", "idiomas", "linguistics", "linguistica", "grammar", "gramatica", "writing", "escritura"],
      topics: [
        { label: "Language learning", kw: ["vocabulary", "vocabulario", "grammar", "gramatica", "english", "ingles", "spanish", "espanol", "french", "frances", "pronunciation", "pronunciacion", "verb", "verbo", "lesson", "leccion"] },
        { label: "Linguistics", kw: ["linguistics", "linguistica", "syntax", "sintaxis", "phonetics", "fonetica", "semantics", "semantica"] },
        { label: "Writing", kw: ["writing", "escritura", "writer", "escritor", "storytelling", "narrative", "creative writing", "escritura creativa"] }
      ] },
    { id: "education", label: "Education & Reference", bisac: ["EDU", "REF", "STU"], thema: ["G", "JN"],
      kw: ["education", "educacion", "reference", "referencia", "study aids", "teaching", "ensenanza", "pedagogia", "dictionary", "diccionario", "encyclopedia", "enciclopedia"],
      topics: [
        { label: "Education & teaching", kw: ["education", "educacion", "teacher", "profesor", "maestro", "student", "estudiante", "alumno", "school", "escuela", "learning", "aprendizaje", "classroom", "aula", "pedagogy", "pedagogia"] },
        { label: "Reference", kw: ["dictionary", "diccionario", "encyclopedia", "enciclopedia", "glossary", "glosario", "handbook"] }
      ] },
    { id: "travel", label: "Travel & Geography", bisac: ["TRV"], thema: ["WT", "RG"],
      kw: ["travel", "viajes", "viaje", "geography", "geografia", "guidebook", "guia de viaje"],
      topics: [
        { label: "Travel", kw: ["travel", "viaje", "viajes", "trip", "journey", "hotel", "tourist", "turista", "itinerary", "itinerario"] },
        { label: "Geography", kw: ["geography", "geografia", "map", "mapa", "continent", "continente"] }
      ] },
    { id: "lifestyle", label: "Lifestyle & Hobbies", bisac: ["CKB", "CRA", "GAR", "HOM", "PET", "GAM"], thema: ["W"],
      kw: ["cooking", "cocina", "recetas", "crafts", "manualidades", "gardening", "jardineria", "house home", "pets", "mascotas", "games", "juegos", "hobbies"],
      topics: [
        { label: "Cooking", kw: ["recipe", "receta", "recipes", "recetas", "cooking", "cocina", "kitchen", "ingredients", "ingredientes", "bake", "hornear", "oven", "horno", "tablespoon", "cucharada"] },
        { label: "Crafts & DIY", kw: ["crafts", "manualidades", "knitting", "tejer", "sewing", "costura", "diy", "woodworking", "carpinteria"] },
        { label: "Gardening", kw: ["garden", "jardin", "gardening", "jardineria", "plants", "plantas", "seeds", "semillas", "soil", "tierra"] },
        { label: "Home & pets", kw: ["pets", "mascotas", "dog", "perro", "cat", "gato", "decoration", "decoracion"] },
        { label: "Games & puzzles", kw: ["games", "juegos", "chess", "ajedrez", "puzzle", "puzzles", "crossword", "crucigrama"] }
      ] },
    { id: "sports", label: "Sports & Outdoors", bisac: ["SPO"], thema: ["S"],
      kw: ["sports", "deportes", "deporte", "recreation", "outdoors"],
      topics: [{ label: "Sports", kw: ["football", "futbol", "soccer", "baseball", "beisbol", "basketball", "baloncesto", "tennis", "tenis", "running", "correr", "marathon", "maraton", "cycling", "ciclismo", "match", "coach", "entrenador", "climbing", "escalada"] }] },
  ];
  // Fiction BISAC codes that say the genre
  const FIC = { FIC028: "Science fiction", FIC009: "Fantasy", FIC022: "Mystery & crime", FIC050: "Mystery & crime", FIC031: "Thriller & suspense", FIC006: "Thriller & suspense",
    FIC027: "Romance", FIC015: "Horror", FIC014: "Historical fiction", FIC019: "Literary fiction", FIC016: "Humour", POE: "Poetry", DRA: "Drama & theatre", CGN: "Comics & graphic novels", HUM: "Humour", LIT: "Literary criticism" };
  const OTHER = { id: "other", label: "Uncategorized" };
  const byId = Object.fromEntries([...AREAS, OTHER].map(a => [a.id, a]));

  // BISAC heading names (the words before the first "/"), in English and Spanish
  const HEADINGS = {
    "fiction": "fiction", "ficcion": "fiction", "novela": "fiction", "poetry": "fiction", "poesia": "fiction", "drama": "fiction", "teatro": "fiction",
    "literary collections": "fiction", "literary criticism": "fiction", "comics graphic novels": "fiction", "humor": "fiction",
    "juvenile fiction": "children", "juvenile nonfiction": "children", "young adult fiction": "children", "young adult nonfiction": "children", "infantil": "children", "juvenil": "children",
    "biography autobiography": "biography", "biografia": "biography", "history": "history", "historia": "history",
    "philosophy": "philosophy", "filosofia": "philosophy", "religion": "philosophy", "body mind spirit": "philosophy",
    "psychology": "psychology", "psicologia": "psychology", "self help": "psychology", "autoayuda": "psychology", "family relationships": "psychology",
    "social science": "society", "political science": "society", "true crime": "society", "ciencias sociales": "society", "politica": "society",
    "business economics": "business", "economia": "business", "negocios": "business", "law": "law", "derecho": "law",
    "science": "science", "ciencia": "science", "mathematics": "science", "matematicas": "science", "nature": "science",
    "medical": "medicine", "medicina": "medicine", "health fitness": "medicine", "salud": "medicine",
    "technology engineering": "technology", "tecnologia": "technology", "transportation": "technology",
    "computers": "computing", "informatica": "computing", "computacion": "computing",
    "art": "arts", "arte": "arts", "music": "arts", "musica": "arts", "architecture": "arts", "design": "arts", "photography": "arts", "performing arts": "arts", "antiques collectibles": "arts",
    "language arts disciplines": "language", "foreign language study": "language", "idiomas": "language",
    "education": "education", "educacion": "education", "reference": "education", "study aids": "education",
    "travel": "travel", "viajes": "travel", "cooking": "lifestyle", "cocina": "lifestyle", "crafts hobbies": "lifestyle", "gardening": "lifestyle", "house home": "lifestyle", "pets": "lifestyle", "games activities": "lifestyle",
    "sports recreation": "sports", "deportes": "sports"
  };

  function countIn(hay, kw) {
    // hay is " word word word " (folded); counts whole-word occurrences of kw
    const needle = " " + kw + " "; let n = 0, i = hay.indexOf(needle);
    while (i !== -1 && n < 10) { n++; i = hay.indexOf(needle, i + needle.length - 1); }
    return n;
  }
  const topicIn = (area, hay) => {
    let best = null, score = 0;
    for (const t of area.topics || []) {
      const s = t.kw.reduce((sum, k) => sum + countIn(hay, words(k)), 0);
      if (s > score) { best = t; score = s; }
    }
    return best ? best.label : null;
  };
  const clean = s => s.replace(/\s+/g, " ").trim().replace(/^./, c => c.toUpperCase());

  /** Area and topic of a book from its analysis. */
  function categorize(a) {
    const subj = " " + a.subjects.map(s => words(s.text)).join(" | ") + " ";
    const subjTitle = subj + words(a.title + " " + a.description) + " ";
    // 1. Subject codes
    for (const s of a.subjects) {
      const code = (s.code || s.text).trim().toUpperCase();
      let area = null;
      let codeTopic = null;
      if (/^[A-Z]{3}\d{6}$/.test(code)) { area = AREAS.find(x => x.bisac.includes(code.slice(0, 3))); codeTopic = FIC[code.slice(0, 6)] || FIC[code.slice(0, 3)] || null; }
      else if ((s.scheme.includes("THEMA") || s.scheme.includes("BIC")) && /^[A-Z][A-Z0-9.]{0,7}$/.test(code)) {
        let len = 0;
        for (const x of AREAS) for (const p of x.thema) if (code.startsWith(p) && p.length > len) { area = x; len = p.length; }
      }
      if (area) {
        const label = s.code && s.text && s.text.toUpperCase() !== code ? s.text : "";
        const last = label.split("/").map(p => p.trim()).filter(p => p && !/^general$/i.test(p)).pop();
        return { area: area.id, topic: codeTopic || topicIn(area, subjTitle) || (last && words(last) !== words(area.label) ? clean(last) : null), source: "metadata" };
      }
    }
    // 2. Subject words: BISAC heading text ("Fiction / Science Fiction / General") or free words
    for (const s of a.subjects) {
      const parts = s.text.split(/\s*[\/>]\s*|\s+--\s+/).map(p => words(p.replace(/&/g, " "))).filter(Boolean);
      const id = parts.length && HEADINGS[parts[0]];
      if (id) {
        const area = byId[id];
        const last = s.text.split(/\s*[\/>]\s*|\s+--\s+/).map(p => p.trim()).filter(p => p && !/^general$/i.test(p)).slice(1).pop();
        return { area: id, topic: topicIn(area, subjTitle) || (last ? clean(last) : null), source: "metadata" };
      }
    }
    if (a.subjects.length) {
      let best = null, score = 0, topic = null;
      for (const area of AREAS) {
        let s = area.kw.reduce((n, k) => n + countIn(subj, words(k)) * 3, 0);
        let t = null, ts = 0;
        for (const tp of area.topics) { const v = tp.kw.reduce((n, k) => n + countIn(subj, words(k)), 0); if (v > ts) { t = tp.label; ts = v; } }
        s += ts;
        if (s > score) { best = area; score = s; topic = t; }
      }
      if (best && score >= 2) return { area: best.id, topic: topic || null, source: "metadata" };
    }
    // 3. The book itself
    const head = " " + words(a.title + " " + a.description) + " ";
    const body = " " + words(a.sample) + " ";
    let best = null, bestScore = 0, bestTopic = null;
    for (const area of AREAS) {
      let s = area.kw.reduce((n, k) => n + countIn(head, words(k)) * 6, 0), t = null, ts = 0;
      for (const tp of area.topics) {
        const v = tp.kw.reduce((n, k) => { const w = words(k); return n + countIn(head, w) * 6 + countIn(body, w); }, 0);
        s += v; if (v > ts) { t = tp.label; ts = v; }
      }
      // Novels are full of dialogue; essays and manuals hardly have any
      if (area.id === "fiction") s += Math.round(Math.max(0, a.dialogue - 0.05) * 400);
      if (s > bestScore) { best = area; bestScore = s; bestTopic = t; }
    }
    if (!best || bestScore < 12) return { area: "other", topic: null, source: "content" };
    if (best.id === "fiction" && !bestTopic) bestTopic = "Literary fiction";
    return { area: best.id, topic: bestTopic, source: "content" };
  }

  window.AtrilCatalog = { version: CATALOG_VERSION, analyze, signature, sameBook, categorize, AREAS: [...AREAS, OTHER], areaLabel: id => (byId[id] || OTHER).label, titleKeys };
})();
