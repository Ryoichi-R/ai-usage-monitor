// P0-3 probe: capture the Claude CLI /usage flow through a PTY, render the VT
// stream into a rectangular screen in memory, count control-sequence categories,
// and write only anonymized fixture candidates that pass forbidden-pattern checks.
//
// Raw bytes and unanonymized screens never leave process memory. The launch,
// signature verification, descendant tracking and termination come from the
// P0-9 probe (descendant-tracker.c).
#define DESCENDANT_TRACKER_NO_MAIN
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wunused-function"
#include "../macos-process-lifecycle/descendant-tracker.c"
#pragma clang diagnostic pop

#include <locale.h>
#include <regex.h>
#include <wchar.h>

#define VT_ROWS 120
#define VT_COLS 400
#define WIDE_TAIL 0xFFFFFFFFu
#define MAX_CATEGORIES 160

// ---------------------------------------------------------------------------
// VT screen model (reference renderer for the PoC, not the product model)
// ---------------------------------------------------------------------------

typedef struct {
    char key[40];
    int count;
    bool supported;
} category_t;

typedef struct {
    uint32_t main_cells[VT_ROWS][VT_COLS];
    uint32_t alt_cells[VT_ROWS][VT_COLS];
    uint32_t (*cells)[VT_COLS];
    bool alt_active;
    int row, col;
    int saved_row, saved_col;
    int top, bottom;
    bool wrap_pending;
    bool autowrap;
    int state;              // 0 ground, 1 ESC, 2 CSI, 3 OSC, 4 string(DCS/APC/PM/SOS), 5 ESC intermediate, 6 string-ESC, 7 OSC-ESC
    char params[64];
    size_t params_length;
    char private_marker;
    char intermediate;
    uint32_t utf8_code;
    int utf8_remaining;
    int master;
    bool reply_da;
    category_t categories[MAX_CATEGORIES];
    int category_count;
    int unsupported;
    int wide_chars;
    int combining_dropped;
    int responses;
} vt_t;

static void vt_count(vt_t *vt, const char *key, bool supported)
{
    for (int index = 0; index < vt->category_count; index++) {
        if (strcmp(vt->categories[index].key, key) == 0) { vt->categories[index].count++; return; }
    }
    if (vt->category_count >= MAX_CATEGORIES) { vt->unsupported++; return; }
    category_t *category = &vt->categories[vt->category_count++];
    (void)snprintf(category->key, sizeof(category->key), "%s", key);
    category->count = 1;
    category->supported = supported;
    if (!supported) vt->unsupported++;
}

static void vt_clear_cells(uint32_t (*cells)[VT_COLS])
{
    for (int row = 0; row < VT_ROWS; row++)
        for (int col = 0; col < VT_COLS; col++) cells[row][col] = ' ';
}

static void vt_init(vt_t *vt, int master, bool reply_da)
{
    memset(vt, 0, sizeof(*vt));
    vt_clear_cells(vt->main_cells);
    vt_clear_cells(vt->alt_cells);
    vt->cells = vt->main_cells;
    vt->bottom = VT_ROWS - 1;
    vt->autowrap = true;
    vt->master = master;
    vt->reply_da = reply_da;
}

static void vt_scroll_up(vt_t *vt, int lines)
{
    for (int step = 0; step < lines; step++) {
        for (int row = vt->top; row < vt->bottom; row++) memcpy(vt->cells[row], vt->cells[row + 1], sizeof(vt->cells[row]));
        for (int col = 0; col < VT_COLS; col++) vt->cells[vt->bottom][col] = ' ';
    }
}

static void vt_scroll_down(vt_t *vt, int lines)
{
    for (int step = 0; step < lines; step++) {
        for (int row = vt->bottom; row > vt->top; row--) memcpy(vt->cells[row], vt->cells[row - 1], sizeof(vt->cells[row]));
        for (int col = 0; col < VT_COLS; col++) vt->cells[vt->top][col] = ' ';
    }
}

static void vt_line_feed(vt_t *vt)
{
    if (vt->row == vt->bottom) vt_scroll_up(vt, 1);
    else if (vt->row < VT_ROWS - 1) vt->row++;
}

static int clamp(int value, int low, int high) { return value < low ? low : value > high ? high : value; }

static void vt_put(vt_t *vt, uint32_t code)
{
    int width = wcwidth((wchar_t)code);
    if (width == 0) { vt->combining_dropped++; return; }
    if (width < 0) width = 1;
    if (width == 2) vt->wide_chars++;
    if (vt->wrap_pending || vt->col + width > VT_COLS) {
        if (vt->autowrap) { vt->col = 0; vt_line_feed(vt); }
        else vt->col = VT_COLS - width;
        vt->wrap_pending = false;
    }
    vt->cells[vt->row][vt->col] = code;
    if (width == 2 && vt->col + 1 < VT_COLS) vt->cells[vt->row][vt->col + 1] = WIDE_TAIL;
    vt->col += width;
    if (vt->col >= VT_COLS) { vt->col = VT_COLS - 1; vt->wrap_pending = true; }
}

static int vt_param(vt_t *vt, int index, int fallback)
{
    int current = 0, value = -1;
    for (size_t position = 0; position <= vt->params_length; position++) {
        char character = position < vt->params_length ? vt->params[position] : ';';
        if (character == ';' || character == ':') {
            if (current == index) return value < 0 ? fallback : value;
            current++;
            value = -1;
        } else if (character >= '0' && character <= '9') {
            value = (value < 0 ? 0 : value * 10) + (character - '0');
            if (value > 100000) value = 100000;
        }
    }
    return fallback;
}

static void vt_reply(vt_t *vt, const char *text)
{
    if (write(vt->master, text, strlen(text)) > 0) vt->responses++;
}

static void vt_erase(vt_t *vt, int row, int from, int to)
{
    for (int col = clamp(from, 0, VT_COLS); col < clamp(to, 0, VT_COLS); col++) vt->cells[row][col] = ' ';
}

static void vt_dec_mode(vt_t *vt, bool set)
{
    int mode = vt_param(vt, 0, 0);
    char key[40];
    (void)snprintf(key, sizeof(key), "CSI ?%d%c", mode, set ? 'h' : 'l');
    switch (mode) {
    case 1049: case 1047: case 47:
        if (set && !vt->alt_active) {
            vt->saved_row = vt->row; vt->saved_col = vt->col;
            vt->cells = vt->alt_cells; vt->alt_active = true; vt_clear_cells(vt->alt_cells);
        } else if (!set && vt->alt_active) {
            vt->cells = vt->main_cells; vt->alt_active = false;
            vt->row = vt->saved_row; vt->col = vt->saved_col;
        }
        vt_count(vt, key, true);
        break;
    case 7:
        vt->autowrap = set;
        vt_count(vt, key, true);
        break;
    case 1: case 12: case 25: case 1000: case 1002: case 1003: case 1004: case 1006: case 2004: case 2026: case 2031:
        vt_count(vt, key, true);  // input/cursor/sync modes: no effect on rendered text
        break;
    default:
        vt_count(vt, key, false);
        break;
    }
}

static void vt_csi(vt_t *vt, char final)
{
    char key[40];
    int count = vt_param(vt, 0, 1);
    if (count < 1) count = 1;
    if (vt->private_marker == '?' && (final == 'h' || final == 'l')) { vt_dec_mode(vt, final == 'h'); return; }
    if (vt->private_marker != 0 || vt->intermediate != 0) {
        (void)snprintf(key, sizeof(key), "CSI %c%c%c", vt->private_marker ? vt->private_marker : ' ', vt->intermediate ? vt->intermediate : ' ', final);
        if (vt->private_marker == '>' && final == 'c') { vt_reply(vt, "\x1b[>0;10;1c"); vt_count(vt, key, true); return; }
        // Kitty keyboard query (CSI ? u), XTVERSION (CSI > q), modifyOtherKeys (CSI > m):
        // queries/settings without effect on rendered text; left unanswered.
        bool known = (vt->private_marker == '?' && final == 'u') || (vt->private_marker == '>' && (final == 'q' || final == 'm' || final == 'u')) ||
                     (vt->private_marker == '<' && final == 'u') || (vt->private_marker == '=' && final == 'u') ||
                     (vt->intermediate == ' ' && final == 'q');
        vt_count(vt, key, known);
        return;
    }
    (void)snprintf(key, sizeof(key), "CSI %c", final);
    vt->wrap_pending = false;
    switch (final) {
    case 'm': break;
    case 'A': vt->row = clamp(vt->row - count, 0, VT_ROWS - 1); break;
    case 'B': case 'e': vt->row = clamp(vt->row + count, 0, VT_ROWS - 1); break;
    case 'C': case 'a': vt->col = clamp(vt->col + count, 0, VT_COLS - 1); break;
    case 'D': vt->col = clamp(vt->col - count, 0, VT_COLS - 1); break;
    case 'E': vt->row = clamp(vt->row + count, 0, VT_ROWS - 1); vt->col = 0; break;
    case 'F': vt->row = clamp(vt->row - count, 0, VT_ROWS - 1); vt->col = 0; break;
    case 'G': case '`': vt->col = clamp(vt_param(vt, 0, 1) - 1, 0, VT_COLS - 1); break;
    case 'd': vt->row = clamp(vt_param(vt, 0, 1) - 1, 0, VT_ROWS - 1); break;
    case 'H': case 'f':
        vt->row = clamp(vt_param(vt, 0, 1) - 1, 0, VT_ROWS - 1);
        vt->col = clamp(vt_param(vt, 1, 1) - 1, 0, VT_COLS - 1);
        break;
    case 'J': {
        int mode = vt_param(vt, 0, 0);
        if (mode == 0) { vt_erase(vt, vt->row, vt->col, VT_COLS); for (int row = vt->row + 1; row < VT_ROWS; row++) vt_erase(vt, row, 0, VT_COLS); }
        else if (mode == 1) { vt_erase(vt, vt->row, 0, vt->col + 1); for (int row = 0; row < vt->row; row++) vt_erase(vt, row, 0, VT_COLS); }
        else for (int row = 0; row < VT_ROWS; row++) vt_erase(vt, row, 0, VT_COLS);
        break;
    }
    case 'K': {
        int mode = vt_param(vt, 0, 0);
        if (mode == 0) vt_erase(vt, vt->row, vt->col, VT_COLS);
        else if (mode == 1) vt_erase(vt, vt->row, 0, vt->col + 1);
        else vt_erase(vt, vt->row, 0, VT_COLS);
        break;
    }
    case 'X': vt_erase(vt, vt->row, vt->col, vt->col + count); break;
    case '@':
        memmove(&vt->cells[vt->row][vt->col + (count < VT_COLS - vt->col ? count : 0)], &vt->cells[vt->row][vt->col],
                sizeof(uint32_t) * (size_t)(VT_COLS - vt->col - (count < VT_COLS - vt->col ? count : 0)));
        vt_erase(vt, vt->row, vt->col, vt->col + count);
        break;
    case 'P': {
        int remaining = VT_COLS - vt->col;
        if (count > remaining) count = remaining;
        memmove(&vt->cells[vt->row][vt->col], &vt->cells[vt->row][vt->col + count], sizeof(uint32_t) * (size_t)(remaining - count));
        vt_erase(vt, vt->row, VT_COLS - count, VT_COLS);
        break;
    }
    case 'L': { int saved = vt->top; vt->top = vt->row; vt_scroll_down(vt, count); vt->top = saved; break; }
    case 'M': { int saved = vt->top; vt->top = vt->row; vt_scroll_up(vt, count); vt->top = saved; break; }
    case 'S': vt_scroll_up(vt, count); break;
    case 'T': vt_scroll_down(vt, count); break;
    case 'r':
        vt->top = clamp(vt_param(vt, 0, 1) - 1, 0, VT_ROWS - 1);
        vt->bottom = clamp(vt_param(vt, 1, VT_ROWS) - 1, vt->top, VT_ROWS - 1);
        vt->row = 0; vt->col = 0;
        break;
    case 's': vt->saved_row = vt->row; vt->saved_col = vt->col; break;
    case 'u': vt->row = vt->saved_row; vt->col = vt->saved_col; break;
    case 'n':
        if (vt_param(vt, 0, 0) == 6) {
            char reply[32];
            (void)snprintf(reply, sizeof(reply), "\x1b[%d;%dR", vt->row + 1, vt->col + 1);
            vt_reply(vt, reply);
        } else if (vt_param(vt, 0, 0) == 5) vt_reply(vt, "\x1b[0n");
        break;
    case 'c': if (vt->reply_da) vt_reply(vt, "\x1b[?62;22c"); break;
    case 't': break;  // window manipulation: no effect on a fixed-size PTY
    default:
        vt_count(vt, key, false);
        return;
    }
    vt_count(vt, key, true);
}

static void vt_esc(vt_t *vt, unsigned char byte)
{
    char key[16];
    (void)snprintf(key, sizeof(key), "ESC %c", byte);
    switch (byte) {
    case '7': vt->saved_row = vt->row; vt->saved_col = vt->col; break;
    case '8': vt->row = vt->saved_row; vt->col = vt->saved_col; break;
    case 'D': vt_line_feed(vt); break;
    case 'E': vt->col = 0; vt_line_feed(vt); break;
    case 'M': if (vt->row == vt->top) vt_scroll_down(vt, 1); else if (vt->row > 0) vt->row--; break;
    case '=': case '>': break;  // keypad modes
    case 'c': vt_clear_cells(vt->cells); vt->row = vt->col = 0; break;
    default: vt_count(vt, key, false); return;
    }
    vt_count(vt, key, true);
}

static void vt_feed(vt_t *vt, const unsigned char *data, size_t length)
{
    for (size_t index = 0; index < length; index++) {
        unsigned char byte = data[index];
        switch (vt->state) {
        case 0:
            if (vt->utf8_remaining > 0) {
                if ((byte & 0xC0) == 0x80) {
                    vt->utf8_code = (vt->utf8_code << 6) | (byte & 0x3F);
                    if (--vt->utf8_remaining == 0) vt_put(vt, vt->utf8_code);
                    continue;
                }
                vt->utf8_remaining = 0;
                vt_count(vt, "UTF8 invalid", false);
            }
            if (byte == 0x1b) { vt->state = 1; continue; }
            if (byte == '\r') { vt->col = 0; vt->wrap_pending = false; vt_count(vt, "C0 CR", true); continue; }
            if (byte == '\n' || byte == 0x0b || byte == 0x0c) { vt_line_feed(vt); vt->wrap_pending = false; vt_count(vt, "C0 LF", true); continue; }
            if (byte == '\b') { if (vt->col > 0) vt->col--; vt->wrap_pending = false; vt_count(vt, "C0 BS", true); continue; }
            if (byte == '\t') { vt->col = clamp((vt->col / 8 + 1) * 8, 0, VT_COLS - 1); vt_count(vt, "C0 HT", true); continue; }
            if (byte == 0x07) { vt_count(vt, "C0 BEL", true); continue; }
            if (byte < 0x20 || byte == 0x7f) { char key[16]; (void)snprintf(key, sizeof(key), "C0 0x%02x", byte); vt_count(vt, key, byte == 0x0e || byte == 0x0f); continue; }
            if (byte < 0x80) { vt_put(vt, byte); continue; }
            if ((byte & 0xE0) == 0xC0) { vt->utf8_code = byte & 0x1F; vt->utf8_remaining = 1; }
            else if ((byte & 0xF0) == 0xE0) { vt->utf8_code = byte & 0x0F; vt->utf8_remaining = 2; }
            else if ((byte & 0xF8) == 0xF0) { vt->utf8_code = byte & 0x07; vt->utf8_remaining = 3; }
            else vt_count(vt, "UTF8 invalid", false);
            break;
        case 1:
            if (byte == '[') { vt->state = 2; vt->params_length = 0; vt->private_marker = 0; vt->intermediate = 0; }
            else if (byte == ']') { vt->state = 3; vt->params_length = 0; vt->params[0] = '\0'; }
            else if (byte == 'P' || byte == '_' || byte == '^' || byte == 'X') { vt->state = 4; vt_count(vt, byte == 'P' ? "DCS" : byte == '_' ? "APC" : "PM/SOS", true); }
            else if (byte == '(' || byte == ')' || byte == '*' || byte == '+' || byte == '#' || byte == '%') vt->state = 5;
            else { vt_esc(vt, byte); vt->state = 0; }
            break;
        case 2:
            if (byte >= 0x40 && byte <= 0x7e) { vt->params[vt->params_length] = '\0'; vt_csi(vt, (char)byte); vt->state = 0; }
            else if (byte == '?' || byte == '>' || byte == '<' || byte == '=') { if (vt->params_length == 0) vt->private_marker = (char)byte; }
            else if (byte >= 0x20 && byte <= 0x2f) vt->intermediate = (char)byte;
            else if (vt->params_length + 1 < sizeof(vt->params)) vt->params[vt->params_length++] = (char)byte;
            break;
        case 3:
            // Only the numeric OSC selector is kept (e.g. "OSC 11"); payloads are discarded.
            if (vt->params_length < 8 && byte >= '0' && byte <= '9' && vt->params[0] != ';') vt->params[vt->params_length++] = (char)byte;
            else if (byte == ';' || byte == 0x07 || byte == 0x1b) {
                if (vt->params_length > 0 && vt->params[0] != ';') {
                    char key[16];
                    vt->params[vt->params_length] = '\0';
                    (void)snprintf(key, sizeof(key), "OSC %s", vt->params);
                    vt_count(vt, key, true);
                }
                vt->params[0] = ';';
                vt->params_length = 1;
            }
            if (byte == 0x07) { vt->state = 0; vt->params_length = 0; vt->params[0] = '\0'; }
            else if (byte == 0x1b) vt->state = 7;
            break;
        case 7:
            vt->state = 0; vt->params_length = 0; vt->params[0] = '\0';
            break;
        case 4:
            if (byte == 0x1b) vt->state = 6;
            break;
        case 6:
            vt->state = byte == '\\' ? 0 : 4;
            break;
        case 5:
            vt_count(vt, "ESC charset", true);
            vt->state = 0;
            break;
        default:
            vt->state = 0;
            break;
        }
    }
}

// Renders the screen as UTF-8 lines, right-trimmed, with leading and trailing
// blank rows removed. Returns the number of bytes written.
static size_t vt_render(vt_t *vt, char *out, size_t capacity, int *line_count)
{
    int first = -1, last = -1;
    for (int row = 0; row < VT_ROWS; row++) {
        for (int col = 0; col < VT_COLS; col++) {
            if (vt->cells[row][col] != ' ' && vt->cells[row][col] != WIDE_TAIL) { if (first < 0) first = row; last = row; break; }
        }
    }
    size_t used = 0;
    *line_count = 0;
    if (first < 0) { out[0] = '\0'; return 0; }
    for (int row = first; row <= last; row++) {
        int end = VT_COLS;
        while (end > 0 && (vt->cells[row][end - 1] == ' ' || vt->cells[row][end - 1] == WIDE_TAIL)) end--;
        for (int col = 0; col < end; col++) {
            uint32_t code = vt->cells[row][col];
            if (code == WIDE_TAIL) continue;
            char encoded[4];
            size_t length;
            if (code < 0x80) { encoded[0] = (char)code; length = 1; }
            else if (code < 0x800) { encoded[0] = (char)(0xC0 | (code >> 6)); encoded[1] = (char)(0x80 | (code & 0x3F)); length = 2; }
            else if (code < 0x10000) { encoded[0] = (char)(0xE0 | (code >> 12)); encoded[1] = (char)(0x80 | ((code >> 6) & 0x3F)); encoded[2] = (char)(0x80 | (code & 0x3F)); length = 3; }
            else { encoded[0] = (char)(0xF0 | (code >> 18)); encoded[1] = (char)(0x80 | ((code >> 12) & 0x3F)); encoded[2] = (char)(0x80 | ((code >> 6) & 0x3F)); encoded[3] = (char)(0x80 | (code & 0x3F)); length = 4; }
            if (used + length + 2 >= capacity) break;
            memcpy(out + used, encoded, length);
            used += length;
        }
        if (used + 2 < capacity) out[used++] = '\n';
        (*line_count)++;
    }
    out[used] = '\0';
    return used;
}

// ---------------------------------------------------------------------------
// Anonymization and forbidden-pattern checks (in memory)
// ---------------------------------------------------------------------------

static const char *const weekday_names[] = { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
                                             "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun", NULL };
static const char *const month_names[] = { "January", "February", "March", "April", "June", "July", "August", "September",
                                           "October", "November", "December", "Jan", "Feb", "Mar", "Apr", "May", "Jun",
                                           "Jul", "Aug", "Sep", "Sept", "Oct", "Nov", "Dec", NULL };

static char account_tokens[8][128];
static int account_token_count;

static void load_account_tokens(void)
{
    struct passwd *account = getpwuid(getuid());
    if (account == NULL) return;
    if (strlen(account->pw_name) >= 3) (void)snprintf(account_tokens[account_token_count++], 128, "%s", account->pw_name);
    char gecos[256];
    (void)snprintf(gecos, sizeof(gecos), "%s", account->pw_gecos != NULL ? account->pw_gecos : "");
    for (char *save = NULL, *word = strtok_r(gecos, " ,", &save); word != NULL && account_token_count < 8; word = strtok_r(NULL, " ,", &save))
        if (strlen(word) >= 3) (void)snprintf(account_tokens[account_token_count++], 128, "%s", word);
}

static bool is_word_char(char character) { return (character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_'; }

// Replaces whole-word, case-insensitive ASCII occurrences of `word`.
static void replace_word(char *text, size_t capacity, const char *word, const char *replacement)
{
    size_t word_length = strlen(word), replacement_length = strlen(replacement);
    char *cursor = text;
    while ((cursor = strcasestr(cursor, word)) != NULL) {
        bool left = cursor == text || !is_word_char(cursor[-1]);
        bool right = !is_word_char(cursor[word_length]);
        if (!left || !right || (strncmp(cursor, replacement, replacement_length) == 0 && word_length == replacement_length)) { cursor += word_length; continue; }
        size_t tail = strlen(cursor + word_length);
        if ((size_t)(cursor - text) + replacement_length + tail + 1 > capacity) return;
        memmove(cursor + replacement_length, cursor + word_length, tail + 1);
        memcpy(cursor, replacement, replacement_length);
        cursor += replacement_length;
    }
}

static void replace_regex(char *text, size_t capacity, const char *pattern, const char *replacement)
{
    regex_t regex;
    if (regcomp(&regex, pattern, REG_EXTENDED) != 0) return;
    size_t offset = 0, replacement_length = strlen(replacement);
    regmatch_t match;
    while (regexec(&regex, text + offset, 1, &match, offset > 0 ? REG_NOTBOL : 0) == 0 && match.rm_eo > match.rm_so) {
        char *start = text + offset + match.rm_so;
        size_t match_length = (size_t)(match.rm_eo - match.rm_so);
        if (match_length == replacement_length && strncmp(start, replacement, match_length) == 0) { offset += (size_t)match.rm_eo; continue; }
        size_t tail = strlen(start + match_length);
        if ((size_t)(start - text) + replacement_length + tail + 1 > capacity) break;
        memmove(start + replacement_length, start + match_length, tail + 1);
        memcpy(start, replacement, replacement_length);
        offset = (size_t)(start - text) + replacement_length;
    }
    regfree(&regex);
}

static int count_regex(const char *text, const char *pattern)
{
    regex_t regex;
    if (regcomp(&regex, pattern, REG_EXTENDED | REG_ICASE | REG_NEWLINE) != 0) return -1;
    int count = 0;
    size_t offset = 0;
    regmatch_t match;
    while (regexec(&regex, text + offset, 1, &match, offset > 0 ? REG_NOTBOL : 0) == 0 && match.rm_eo > match.rm_so) {
        count++;
        offset += (size_t)match.rm_eo;
    }
    regfree(&regex);
    return count;
}

// Usage-breakdown rows ("<name> NN%", e.g. skills, agents, plugins) reveal what the
// account uses. Their names are replaced with numbered dummies before digit masking.
static void anonymize_breakdown_names(char *text, size_t capacity)
{
    static char output[sizeof(((vt_t *)0)->main_cells) + 8192];
    regex_t row;
    if (regcomp(&row, "^[^ ]+ +[0-9]+(\\.[0-9]+)?%$", REG_EXTENDED) != 0) return;
    size_t used = 0;
    int slash_index = 0, item_index = 0;
    char *line = text;
    while (*line != '\0') {
        char *end = strchr(line, '\n');
        size_t length = end != NULL ? (size_t)(end - line) : strlen(line);
        char saved = line[length];
        line[length] = '\0';
        char replaced[512];
        const char *emit = line;
        if (regexec(&row, line, 0, NULL, 0) == 0) {
            const char *rest = strchr(line, ' ');
            if (line[0] == '/') (void)snprintf(replaced, sizeof(replaced), "/skill-%c%s", 'a' + (slash_index++ % 26), rest);
            else (void)snprintf(replaced, sizeof(replaced), "item-%c%s", 'a' + (item_index++ % 26), rest);
            emit = replaced;
        }
        size_t emit_length = strlen(emit);
        if (used + emit_length + 2 < sizeof(output)) { memcpy(output + used, emit, emit_length); used += emit_length; if (end != NULL) output[used++] = '\n'; }
        line[length] = saved;
        if (end == NULL) break;
        line = end + 1;
    }
    output[used] = '\0';
    regfree(&row);
    if (used < capacity) memcpy(text, output, used + 1);
    memset(output, 0, used);
}

// mode '#': classification fixture; mode '1': parser fixture with fixed dummy digits.
static void anonymize(char *text, size_t capacity, char digit_replacement)
{
    anonymize_breakdown_names(text, capacity);
    replace_regex(text, capacity, "Claude (Pro|Max|Team|Enterprise|Free)", "Claude Pro");
    replace_regex(text, capacity, "/Users/[A-Za-z0-9._-]+", "~");
    replace_regex(text, capacity, "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}", "user@example.com");
    for (int index = 0; index < account_token_count; index++) replace_word(text, capacity, account_tokens[index], "User");
    for (int index = 0; weekday_names[index] != NULL; index++) replace_word(text, capacity, weekday_names[index], "Sun");
    for (int index = 0; month_names[index] != NULL; index++) replace_word(text, capacity, month_names[index], "Aug");
    // Time zones other than the product-supported Asia/Tokyo become a fixed dummy.
    replace_regex(text, capacity, "\\((Africa|America|Antarctica|Arctic|Atlantic|Australia|Europe|Indian|Pacific|Etc|Asia)/[A-Za-z_+-]+\\)", "(ZONE)");
    replace_regex(text, capacity, "\\(ZONE\\)", "(Asia/Tokyo)");
    for (char *cursor = text; *cursor != '\0'; cursor++)
        if (*cursor >= '0' && *cursor <= '9') *cursor = digit_replacement;
}

typedef struct { int digits; int emails; int account; int users_path; int token_like; int weekday; int month; int breakdown; } violations_t;

static violations_t check_forbidden(const char *text, char digit_replacement)
{
    violations_t result = { 0, 0, 0, 0, 0, 0, 0, 0 };
    int rows = count_regex(text, "^[^ ]+ +[0-9#]+(\\.[0-9#]+)?%$");
    int dummy_rows = count_regex(text, "^(/skill|item)-[a-z] +[0-9#]+(\\.[0-9#]+)?%$");
    result.breakdown = rows - dummy_rows;
    for (const char *cursor = text; *cursor != '\0'; cursor++)
        if (*cursor >= '0' && *cursor <= '9' && *cursor != digit_replacement) result.digits++;
    int emails = count_regex(text, "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}");
    int dummy = count_regex(text, "user@example\\.com");
    result.emails = emails - dummy;
    for (int index = 0; index < account_token_count; index++) {
        char pattern[160];
        (void)snprintf(pattern, sizeof(pattern), "(^|[^A-Za-z0-9_])%s([^A-Za-z0-9_]|$)", account_tokens[index]);
        result.account += count_regex(text, pattern);
    }
    result.users_path = count_regex(text, "/Users/");
    result.token_like = count_regex(text, "[A-Za-z0-9_-]{32,}");
    for (int index = 0; weekday_names[index] != NULL; index++) {
        if (strcmp(weekday_names[index], "Sun") == 0) continue;
        char pattern[64];
        (void)snprintf(pattern, sizeof(pattern), "(^|[^A-Za-z])%s([^A-Za-z]|$)", weekday_names[index]);
        result.weekday += count_regex(text, pattern);
    }
    for (int index = 0; month_names[index] != NULL; index++) {
        if (strcmp(month_names[index], "Aug") == 0) continue;
        char pattern[64];
        (void)snprintf(pattern, sizeof(pattern), "(^|[^A-Za-z])%s([^A-Za-z]|$)", month_names[index]);
        result.month += count_regex(text, pattern);
    }
    return result;
}

static int violation_total(violations_t value)
{
    return value.digits + value.emails + value.account + value.users_path + value.token_like + value.weekday + value.month + value.breakdown;
}

// ---------------------------------------------------------------------------
// Capture flow
// ---------------------------------------------------------------------------

static char rendered[VT_ROWS * VT_COLS * 4 + VT_ROWS + 1];

static bool pump_vt(tracker_t *tracker, vt_t *vt, int milliseconds, int64_t *last_output)
{
    bool received_any = false;
    int64_t deadline = now_ms() + milliseconds;
    do {
        struct pollfd poll_fd = { vt->master, POLLIN, 0 };
        (void)poll(&poll_fd, 1, 10);
        unsigned char buffer[8192];
        for (;;) {
            ssize_t count = read(vt->master, buffer, sizeof(buffer));
            if (count <= 0) break;
            vt_feed(vt, buffer, (size_t)count);
            memset(buffer, 0, (size_t)count);
            received_any = true;
            if (last_output != NULL) *last_output = now_ms();
        }
        tracker_pump(tracker, 0);
    } while (now_ms() < deadline);
    return received_any;
}

static void render_now(vt_t *vt, int *line_count)
{
    (void)vt_render(vt, rendered, sizeof(rendered), line_count);
}

// Waits until the rendered screen satisfies `predicate` and output has been quiet for quiet_ms.
typedef bool (*screen_predicate_t)(const char *text);

static bool wait_screen(tracker_t *tracker, vt_t *vt, screen_predicate_t predicate, int timeout_ms, int quiet_ms)
{
    int64_t deadline = now_ms() + timeout_ms, last_output = now_ms();
    int lines = 0;
    while (now_ms() < deadline && !tracker->root_reaped) {
        (void)pump_vt(tracker, vt, 100, &last_output);
        if (now_ms() - last_output < quiet_ms) continue;
        render_now(vt, &lines);
        if (predicate(rendered)) return true;
    }
    render_now(vt, &lines);
    return predicate(rendered);
}

static bool any_classified(const char *text)
{
    return contains_any(text, trust_anchors) || contains_any(text, signed_out_anchors) || contains_any(text, setup_anchors) ||
           (contains_any(text, ready_anchors) && has_prompt_line(text));
}

static bool usage_visible(const char *text) { return contains_any(text, usage_anchors); }
static bool usage_gone(const char *text) { return !contains_any(text, usage_anchors); }

static const char *output_dir;
static int written_files;

static void emit_candidate(const char *name, const char *text_in, int line_count)
{
    static char text[sizeof(rendered) + 4096];
    for (int variant = 0; variant < 2; variant++) {
        char digit = variant == 0 ? '#' : '1';
        (void)snprintf(text, sizeof(text), "%s", text_in);
        anonymize(text, sizeof(text), digit);
        violations_t violations = check_forbidden(text, digit);
        int total = violation_total(violations);
        size_t max_width = 0, current = 0;
        for (const unsigned char *cursor = (const unsigned char *)text; *cursor != '\0'; cursor++) {
            if (*cursor == '\n') { if (current > max_width) max_width = current; current = 0; }
            else if ((*cursor & 0xC0) != 0x80) current++;
        }
        printf("CANDIDATE %s variant=%s lines=%d max_chars=%zu forbidden=%d (digits=%d emails=%d account=%d users_path=%d token_like=%d weekday=%d month=%d breakdown=%d)",
               name, variant == 0 ? "masked" : "dummy", line_count, max_width, total, violations.digits, violations.emails,
               violations.account, violations.users_path, violations.token_like, violations.weekday, violations.month, violations.breakdown);
        if (total == 0 && output_dir != NULL) {
            char path[PATH_MAX];
            (void)snprintf(path, sizeof(path), "%s/%s%s.txt", output_dir, name, variant == 0 ? "" : ".parse");
            int fd = open(path, O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | O_NOFOLLOW, 0600);
            if (fd >= 0) {
                size_t length = strlen(text);
                if (write(fd, text, length) == (ssize_t)length) { written_files++; printf(" written=1"); }
                close(fd);
            }
        } else {
            printf(" written=0");
        }
        printf("\n");
        memset(text, 0, sizeof(text));
    }
}

static int capture_helper(const char *team_id, const char *binary, const char *cwd, bool reply_da, int argc, char **argv)
{
    if (setsid() < 0) die("setsid");
    signal(SIGTTOU, SIG_IGN);
    signal(SIGPIPE, SIG_IGN);
    signal(SIGHUP, SIG_IGN);
    load_account_tokens();

    char requirement_text[512];
    (void)snprintf(requirement_text, sizeof(requirement_text),
        "identifier \"com.anthropic.claude-code\" and anchor apple generic and "
        "certificate 1[field.1.2.840.113635.100.6.2.6] exists and "
        "certificate leaf[field.1.2.840.113635.100.6.1.13] exists and "
        "certificate leaf[subject.OU] = \"%s\"", team_id);
    CFStringRef requirement_string = CFStringCreateWithCString(NULL, requirement_text, kCFStringEncodingUTF8);
    verifier_t verifier = { NULL, NULL };
    if (SecRequirementCreateWithString(requirement_string, kSecCSDefaultFlags, &verifier.requirement) != errSecSuccess) die("requirement");
    CFRelease(requirement_string);
    int fd = open(binary, O_RDONLY | O_CLOEXEC);
    if (fd < 0) die("open binary");
    OSStatus static_status = verify_static_fd(fd, &verifier);
    printf("VERIFY static=%d\n", (int)static_status);
    if (static_status != errSecSuccess) return 3;

    static tracker_t tracker;
    tracker_init(&tracker, NULL);
    struct passwd *account = getpwuid(getuid());
    static char home[PATH_MAX + 8], user[256], logname[256], tmpdir[PATH_MAX + 8];
    (void)snprintf(home, sizeof(home), "HOME=%s", account->pw_dir);
    (void)snprintf(user, sizeof(user), "USER=%s", account->pw_name);
    (void)snprintf(logname, sizeof(logname), "LOGNAME=%s", account->pw_name);
    const char *temporary = getenv("TMPDIR");
    (void)snprintf(tmpdir, sizeof(tmpdir), "TMPDIR=%s", temporary != NULL ? temporary : "/private/tmp/");
    // AIUSAGE_CAPTURE_LANG selects the CLI locale (e.g. ja_JP.UTF-8); only UTF-8 locales are accepted.
    static char lang[64] = "LANG=en_US.UTF-8";
    const char *capture_lang = getenv("AIUSAGE_CAPTURE_LANG");
    if (capture_lang != NULL && strlen(capture_lang) < 40 && strstr(capture_lang, ".UTF-8") != NULL)
        (void)snprintf(lang, sizeof(lang), "LANG=%s", capture_lang);
    printf("LOCALE %s\n", lang + 5);
    char *envp[] = { home, user, logname, tmpdir, "PATH=/usr/bin:/bin:/usr/sbin:/sbin", lang,
                     "TERM=xterm-256color", tracker.token, NULL };
    char *child_argv[64];
    int child_argc = 0;
    child_argv[child_argc++] = (char *)binary;
    for (int index = 0; index < argc && child_argc < 63; index++) child_argv[child_argc++] = argv[index];
    child_argv[child_argc] = NULL;

    launch_t launch = launch_tracked(&tracker, true, binary, child_argv, envp, cwd, &verifier);
    printf("LAUNCH stopped_before_user_code=%d dynamic_signature_and_unique=%d\n", launch.stopped_before_user_code, (int)launch.dynamic_status);
    if (launch.pid < 0) return 3;

    static vt_t vt;
    vt_init(&vt, launch.io_fd, reply_da);
    int lines = 0;
    bool classified = wait_screen(&tracker, &vt, any_classified, 30000, 3000);
    bool trust = contains_any(rendered, trust_anchors);
    bool ready = classified && !trust && !contains_any(rendered, signed_out_anchors) && !contains_any(rendered, setup_anchors);
    render_now(&vt, &lines);
    printf("STATE initial classified=%d trust=%d ready=%d alt_screen=%d\n", classified, trust, ready, vt.alt_active);
    emit_candidate(trust ? "trust-prompt-macos" : "ready-macos", rendered, lines);
    bool usage = false, returned = false;
    if (ready) {
        if (write(launch.io_fd, "/usage", 6) < 0) { }
        (void)pump_vt(&tracker, &vt, 400, NULL);
        if (write(launch.io_fd, "\r", 1) < 0) { }
        usage = wait_screen(&tracker, &vt, usage_visible, 15000, 1500);
        render_now(&vt, &lines);
        printf("STATE usage visible=%d alt_screen=%d\n", usage, vt.alt_active);
        if (usage) emit_candidate("usage-screen-macos", rendered, lines);
        if (write(launch.io_fd, "\x1b", 1) < 0) { }
        returned = wait_screen(&tracker, &vt, usage_gone, 5000, 1500);
        render_now(&vt, &lines);
        bool ready_again = !contains_any(rendered, usage_anchors) && contains_any(rendered, ready_anchors) && has_prompt_line(rendered);
        printf("STATE after_escape usage_gone=%d ready_again=%d\n", returned, ready_again);
        emit_candidate("after-escape-macos", rendered, lines);
    }
    memset(rendered, 0, sizeof(rendered));
    memset(&vt.main_cells, 0, sizeof(vt.main_cells));
    memset(&vt.alt_cells, 0, sizeof(vt.alt_cells));

    printf("VT unsupported=%d wide_chars=%d combining_dropped=%d responses=%d reply_da=%d\n", vt.unsupported, vt.wide_chars,
           vt.combining_dropped, vt.responses, reply_da);
    for (int index = 0; index < vt.category_count; index++)
        printf("VTCAT %-12s count=%d supported=%d\n", vt.categories[index].key, vt.categories[index].count, vt.categories[index].supported);
    print_process_report(&tracker);
    bool term_sufficient = false;
    int residual = tracker_terminate(&tracker, 3000, 3000, &term_sufficient);
    printf("TERMINATE term_within_grace=%d residual_tracked=%d\n", term_sufficient, residual);
    printf("RESULT usage=%d escapes=%d residual=%d files_written=%d\n", usage, tracker.escapes, residual, written_files);
    fflush(stdout);
    return usage && tracker.escapes == 0 && residual == 0 ? 0 : 4;
}

// Synthetic negative controls: each forbidden category must be detected before
// anonymization and removed after it, for both fixture variants.
static int anonymizer_selfcheck(void)
{
    (void)snprintf(account_tokens[0], 128, "%s", "Alicetest");
    account_token_count = 1;
    const char *sample =
        "Welcome back Alicetest\n"
        "alice.test@example.org · Claude Max\n"
        "/Users/alicetest/project\n"
        "Current session\n"
        "37% 37% used\n"
        "Resets 3pm (Europe/Berlin)\n"
        "Current week (all models)\n"
        "Resets Tuesday Sep 30 at 11:45\n"
        "/private-skill-name 12%\n"
        "custom-agent 7%\n"
        "token abcdefghijklmnopqrstuvwxyzABCDEFGH\n";
    static char text[4096];
    bool ok = true;
    for (int variant = 0; variant < 2; variant++) {
        char digit = variant == 0 ? '#' : '1';
        (void)snprintf(text, sizeof(text), "%s", sample);
        violations_t before = check_forbidden(text, digit);
        anonymize(text, sizeof(text), digit);
        violations_t after = check_forbidden(text, digit);
        bool detected = before.digits > 0 && before.emails > 0 && before.account > 0 && before.users_path > 0 &&
                        before.token_like > 0 && before.weekday > 0 && before.month > 0 && before.breakdown == 2;
        printf("SELFCHECK variant=%s before(digits=%d emails=%d account=%d users_path=%d token_like=%d weekday=%d month=%d breakdown=%d) after_total=%d\n",
               variant == 0 ? "masked" : "dummy", before.digits, before.emails, before.account, before.users_path, before.token_like,
               before.weekday, before.month, before.breakdown, violation_total(after));
        // The long token is not rewritten by design (not an expected screen element), so
        // it must remain a detected violation that blocks the write.
        ok &= detected && violation_total(after) == after.token_like && after.token_like == 1;
        ok &= strstr(text, "(Asia/Tokyo)") != NULL && strstr(text, "/skill-a") != NULL && strstr(text, "item-a") != NULL;
    }
    printf("SELFCHECK %s\n", ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}

int main(int argc, char **argv)
{
    uint32_t size = sizeof(self_path);
    if (_NSGetExecutablePath(self_path, &size) != 0) die("executable path");
    setvbuf(stdout, NULL, _IOLBF, 0);
    if (setlocale(LC_CTYPE, "en_US.UTF-8") == NULL) die("locale");
    if (argc == 2 && strcmp(argv[1], "--selfcheck") == 0) return anonymizer_selfcheck();
    if (argc < 5) {
        fprintf(stderr, "usage: %s <team-id> <absolute-binary> <cwd> <output-dir|-> [cli args...]\n"
                        "  AIUSAGE_NO_DA_REPLY=1 leaves primary device-attribute queries unanswered\n", argv[0]);
        return 64;
    }
    if (argv[2][0] != '/' || argv[3][0] != '/') { fprintf(stderr, "binary and cwd must be absolute\n"); return 64; }
    if (strcmp(argv[4], "-") != 0) {
        struct stat metadata;
        if (lstat(argv[4], &metadata) != 0 || !S_ISDIR(metadata.st_mode) || metadata.st_uid != getuid() || (metadata.st_mode & 0077) != 0) {
            fprintf(stderr, "output dir must be an existing owner-only directory\n");
            return 64;
        }
        output_dir = argv[4];
    }
    const char *no_reply = getenv("AIUSAGE_NO_DA_REPLY");
    bool reply_da = !(no_reply != NULL && strcmp(no_reply, "1") == 0);
    pid_t helper = fork();
    if (helper < 0) die("fork");
    if (helper == 0) {
        int code = capture_helper(argv[1], argv[2], argv[3], reply_da, argc - 5, argv + 5);
        fflush(stdout);
        _exit(code);
    }
    int status = 0;
    (void)waitpid(helper, &status, 0);
    return WIFEXITED(status) ? WEXITSTATUS(status) : 5;
}
