#include <Windows.h>
#include <fcntl.h>
#include <io.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <iomanip>
#include <iostream>
#include <limits>
#include <mutex>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

extern "C"
{
#include "wapi.h"
#include "wext.h"
}

namespace
{
constexpr std::string_view protocolReady = "READY";
constexpr std::size_t maximumCommandLength = 1024U * 1024U;
constexpr std::size_t maximumIdentifierLength = 128U;
constexpr std::size_t maximumTokenNameLength = 512U;
constexpr std::size_t maximumBatchItems = 512U;
constexpr int eventBatchSize = 256;
constexpr std::size_t maximumCommandsPerLoop = 16U;
constexpr int eventPollTimeoutMicroseconds = 1'000;
constexpr int scalarSnapshotTimeoutMicroseconds = 1'000'000;
constexpr auto idlePollInterval = std::chrono::milliseconds(10);

struct CommandQueue
{
    std::mutex mutex;
    std::condition_variable changed;
    std::deque<std::string> lines;
    bool inputEnded = false;
};

std::string lowerAscii(std::string_view value)
{
    std::string result;
    result.reserve(value.size());
    for (const unsigned char character : value)
    {
        if (character >= 'A' && character <= 'Z')
        {
            result.push_back(static_cast<char>(character - 'A' + 'a'));
        }
        else
        {
            result.push_back(static_cast<char>(character));
        }
    }
    return result;
}

std::string enumStyleAlias(std::string_view value)
{
    std::string result;
    result.reserve(value.size());

    while (!value.empty() && value.front() == '/')
    {
        value.remove_prefix(1);
    }

    for (const unsigned char character : value)
    {
        if (character == '.' || character == '/')
        {
            result.push_back('_');
        }
        else if (character >= 'a' && character <= 'z')
        {
            result.push_back(static_cast<char>(character - 'a' + 'A'));
        }
        else
        {
            result.push_back(static_cast<char>(character));
        }
    }
    return result;
}

std::vector<std::string_view> splitFields(const std::string& line)
{
    std::vector<std::string_view> fields;
    std::size_t start = 0;
    while (true)
    {
        const std::size_t separator = line.find('|', start);
        if (separator == std::string::npos)
        {
            fields.emplace_back(line.data() + start, line.size() - start);
            return fields;
        }
        fields.emplace_back(line.data() + start, separator - start);
        start = separator + 1;
    }
}

bool isValidIdentifier(std::string_view identifier)
{
    if (identifier.empty() || identifier.size() > maximumIdentifierLength)
    {
        return false;
    }

    return std::all_of(identifier.begin(), identifier.end(), [](const unsigned char character)
    {
        return character >= 0x21U && character <= 0x7eU && character != '|';
    });
}

bool isValidIpv4(std::string_view ip)
{
    if (ip.empty())
    {
        // The official SDK interprets an empty address as network discovery.
        return true;
    }

    int segments = 0;
    std::size_t start = 0;
    while (start <= ip.size())
    {
        const std::size_t dot = ip.find('.', start);
        const std::size_t end = dot == std::string_view::npos ? ip.size() : dot;
        const std::string_view segment = ip.substr(start, end - start);
        if (segment.empty() || segment.size() > 3)
        {
            return false;
        }

        int number = 0;
        const auto parsed = std::from_chars(segment.data(), segment.data() + segment.size(), number);
        if (parsed.ec != std::errc{} || parsed.ptr != segment.data() + segment.size() ||
            number < 0 || number > 255)
        {
            return false;
        }

        ++segments;
        if (dot == std::string_view::npos)
        {
            break;
        }
        start = dot + 1;
    }
    return segments == 4;
}

constexpr std::array<char, 64> base64Alphabet()
{
    std::array<char, 64> alphabet{};
    constexpr std::string_view source =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::copy(source.begin(), source.end(), alphabet.begin());
    return alphabet;
}

std::string base64Encode(std::string_view input)
{
    constexpr auto alphabet = base64Alphabet();
    std::string output;
    output.reserve(((input.size() + 2U) / 3U) * 4U);

    std::size_t index = 0;
    while (index + 3U <= input.size())
    {
        const auto first = static_cast<unsigned char>(input[index++]);
        const auto second = static_cast<unsigned char>(input[index++]);
        const auto third = static_cast<unsigned char>(input[index++]);

        output.push_back(alphabet[first >> 2U]);
        output.push_back(alphabet[((first & 0x03U) << 4U) | (second >> 4U)]);
        output.push_back(alphabet[((second & 0x0fU) << 2U) | (third >> 6U)]);
        output.push_back(alphabet[third & 0x3fU]);
    }

    const std::size_t remaining = input.size() - index;
    if (remaining == 1U)
    {
        const auto first = static_cast<unsigned char>(input[index]);
        output.push_back(alphabet[first >> 2U]);
        output.push_back(alphabet[(first & 0x03U) << 4U]);
        output.append("==");
    }
    else if (remaining == 2U)
    {
        const auto first = static_cast<unsigned char>(input[index]);
        const auto second = static_cast<unsigned char>(input[index + 1U]);
        output.push_back(alphabet[first >> 2U]);
        output.push_back(alphabet[((first & 0x03U) << 4U) | (second >> 4U)]);
        output.push_back(alphabet[(second & 0x0fU) << 2U]);
        output.push_back('=');
    }
    return output;
}

std::optional<std::string> base64Decode(std::string_view input)
{
    if (input.size() % 4U != 0U)
    {
        return std::nullopt;
    }

    std::array<int, 256> lookup{};
    lookup.fill(-1);
    constexpr auto alphabet = base64Alphabet();
    for (std::size_t index = 0; index < alphabet.size(); ++index)
    {
        lookup[static_cast<unsigned char>(alphabet[index])] = static_cast<int>(index);
    }

    std::string output;
    output.reserve((input.size() / 4U) * 3U);
    for (std::size_t index = 0; index < input.size(); index += 4U)
    {
        const bool finalBlock = index + 4U == input.size();
        const char thirdCharacter = input[index + 2U];
        const char fourthCharacter = input[index + 3U];
        const bool thirdPadding = thirdCharacter == '=';
        const bool fourthPadding = fourthCharacter == '=';

        if ((!finalBlock && (thirdPadding || fourthPadding)) ||
            (thirdPadding && !fourthPadding))
        {
            return std::nullopt;
        }

        const int first = lookup[static_cast<unsigned char>(input[index])];
        const int second = lookup[static_cast<unsigned char>(input[index + 1U])];
        const int third = thirdPadding ? 0 : lookup[static_cast<unsigned char>(thirdCharacter)];
        const int fourth = fourthPadding ? 0 : lookup[static_cast<unsigned char>(fourthCharacter)];
        if (first < 0 || second < 0 || third < 0 || fourth < 0)
        {
            return std::nullopt;
        }

        if (thirdPadding && ((second & 0x0f) != 0))
        {
            return std::nullopt;
        }
        if (fourthPadding && !thirdPadding && ((third & 0x03) != 0))
        {
            return std::nullopt;
        }

        output.push_back(static_cast<char>((first << 2) | (second >> 4)));
        if (!thirdPadding)
        {
            output.push_back(static_cast<char>(((second & 0x0f) << 4) | (third >> 2)));
        }
        if (!fourthPadding)
        {
            output.push_back(static_cast<char>(((third & 0x03) << 6) | fourth));
        }
    }
    return output;
}

std::optional<int> parseInt32(std::string_view value)
{
    int parsedValue = 0;
    const auto result = std::from_chars(
        value.data(), value.data() + value.size(), parsedValue, 10);
    if (value.empty() || result.ec != std::errc{} ||
        result.ptr != value.data() + value.size())
    {
        return std::nullopt;
    }
    return parsedValue;
}

std::optional<float> parseFloat32(std::string_view value)
{
    float parsedValue = 0.0F;
    const auto result = std::from_chars(
        value.data(), value.data() + value.size(), parsedValue,
        std::chars_format::general);
    if (value.empty() || result.ec != std::errc{} ||
        result.ptr != value.data() + value.size() || !std::isfinite(parsedValue))
    {
        return std::nullopt;
    }
    return parsedValue;
}

std::string formatFloat(float value)
{
    std::array<char, 64> buffer{};
    const auto result = std::to_chars(
        buffer.data(), buffer.data() + buffer.size(), value,
        std::chars_format::general, std::numeric_limits<float>::max_digits10);
    if (result.ec == std::errc{})
    {
        return std::string(buffer.data(), result.ptr);
    }

    std::ostringstream fallback;
    fallback.imbue(std::locale::classic());
    fallback << std::setprecision(std::numeric_limits<float>::max_digits10) << value;
    return fallback.str();
}

std::string wapiErrorMessage(int code)
{
    switch (code)
    {
    case WSNDL_NTW_ERROR: return "socket send linger/keepalive option failed";
    case WSKAL_NTW_ERROR: return "socket keepalive option failed";
    case WSCON_NTW_ERROR: return "network socket connection failed";
    case WSOCK_NTW_ERROR: return "network socket creation failed";
    case WWSA_NTW_ERROR: return "Windows network operation failed";
    case WTYPE: return "invalid WAPI data type";
    case WHASH: return "unknown WAPI hash";
    case WNODEPARAM: return "WAPI node parameter parse failed";
    case WNODEVALUE: return "WAPI node value parse failed";
    case WRECV_BUF_ERROR: return "WAPI receive buffer overflow";
    case WMEMORY: return "WAPI memory allocation failed";
    case WBIND_UDP_ERROR: return "WAPI UDP bind failed";
    case WSEND_NTW_ERROR: return "WAPI network send failed";
    case WSEND_UDP_ERROR: return "WAPI UDP send failed";
    case WSEND_TCP_ERROR: return "WAPI TCP send failed";
    case WSEND_ERROR: return "WAPI send failed";
    case WRECV_ERROR: return "WAPI receive failed";
    case WTOKEN: return "invalid WAPI token";
    case WNODE: return "invalid WAPI node";
    case WZERO: return "WAPI operation returned no data";
    case WSUCCESS: return "success";
    default: return "unknown WAPI error";
    }
}

class TokenIndex
{
public:
    TokenIndex()
    {
        const int lastToken = static_cast<int>($GLOBALS_CUSTSYNC_C);
        canonicalNames_.resize(static_cast<std::size_t>(lastToken) + 1U);
        aliases_.reserve(static_cast<std::size_t>(lastToken) * 3U);

        for (int rawToken = 1; rawToken <= lastToken; ++rawToken)
        {
            const auto token = static_cast<wtoken>(rawToken);
            const char* const namePointer = wGetName(token);
            if (namePointer == nullptr)
            {
                continue;
            }

            std::string name(namePointer);
            if (name.empty() || lowerAscii(name) == "$$unknown")
            {
                continue;
            }

            canonicalNames_[static_cast<std::size_t>(rawToken)] = name;
            addAlias(name, token);

            std::string withoutSlash = name;
            while (!withoutSlash.empty() && withoutSlash.front() == '/')
            {
                withoutSlash.erase(withoutSlash.begin());
            }
            addAlias(withoutSlash, token);

            std::replace(withoutSlash.begin(), withoutSlash.end(), '/', '.');
            addAlias(withoutSlash, token);
            addAlias(enumStyleAlias(name), token);
        }
    }

    [[nodiscard]] std::optional<wtoken> find(std::string_view name) const
    {
        if (name.empty() || name.size() > maximumTokenNameLength)
        {
            return std::nullopt;
        }

        const auto exact = aliases_.find(lowerAscii(name));
        if (exact != aliases_.end())
        {
            return exact->second;
        }

        const auto enumAlias = aliases_.find(lowerAscii(enumStyleAlias(name)));
        if (enumAlias != aliases_.end())
        {
            return enumAlias->second;
        }
        return std::nullopt;
    }

    [[nodiscard]] const std::string& canonicalName(wtoken token) const
    {
        const auto index = static_cast<std::size_t>(token);
        if (index < canonicalNames_.size() && !canonicalNames_[index].empty())
        {
            return canonicalNames_[index];
        }
        static const std::string unknown = "$$unknown";
        return unknown;
    }

    [[nodiscard]] std::size_t capacity() const
    {
        return canonicalNames_.size() + 1024U;
    }

    [[nodiscard]] std::size_t size() const
    {
        return canonicalNames_.size();
    }

    [[nodiscard]] bool isReadOnly(wtoken token) const
    {
        const std::string name = lowerAscii(canonicalName(token));
        const std::size_t leafStart = name.find_last_of("./");
        const std::size_t leafIndex =
            leafStart == std::string::npos ? 0U : leafStart + 1U;
        if (leafIndex < name.size() && name[leafIndex] == '$')
        {
            return true;
        }

        // WING's status subtrees contain read-only leaves whose individual
        // names do not all carry '$'. Other '$'-prefixed roots such as
        // $syscfg, $ctl and $globals also contain writable leaves and must not
        // be rejected merely because of their root name.
        return name.starts_with("$stat.") ||
            name.find(".$stat.") != std::string::npos;
    }

private:
    void addAlias(std::string_view alias, wtoken token)
    {
        if (!alias.empty())
        {
            aliases_.try_emplace(lowerAscii(alias), token);
        }
    }

    std::unordered_map<std::string, wtoken> aliases_;
    std::vector<std::string> canonicalNames_;
};

struct ParsedSetValue
{
    wtoken token = $$UNKNOWN;
    wtype type = UNKN;
    int integerValue = 0;
    float floatValue = 0.0F;
    std::string stringValue;
};

struct ValidationError
{
    std::string code;
    std::string message;
};

bool parseSetValue(
    const TokenIndex& tokenIndex,
    std::string_view tokenName,
    std::string_view type,
    std::string_view encodedValue,
    ParsedSetValue& output,
    ValidationError& error)
{
    output = {};
    const std::optional<wtoken> token = tokenIndex.find(tokenName);
    if (!token)
    {
        error = {"BAD_TOKEN", "unknown WAPI token name"};
        return false;
    }
    if (wGetType(*token) == NODE)
    {
        error = {"BAD_TOKEN", "SET cannot target a node"};
        return false;
    }
    if (tokenIndex.isReadOnly(*token))
    {
        error = {"READ_ONLY", "SET cannot target a read-only WAPI token"};
        return false;
    }

    wtype requestedType = UNKN;
    if (type == "I")
    {
        requestedType = I32;
    }
    else if (type == "F")
    {
        requestedType = F32;
    }
    else if (type == "S")
    {
        requestedType = S32;
    }
    else
    {
        error = {"BAD_TYPE", "SET type must be I, F, or S"};
        return false;
    }

    const wtype tokenType = wGetType(*token);
    if (tokenType != requestedType)
    {
        error = {"BAD_TYPE", "SET type does not match the WAPI token type"};
        return false;
    }

    const std::optional<std::string> decoded = base64Decode(encodedValue);
    if (!decoded)
    {
        error = {"BAD_BASE64", "value is not canonical base64"};
        return false;
    }

    output.token = *token;
    if (type == "I")
    {
        const std::optional<int> value = parseInt32(*decoded);
        if (!value)
        {
            error = {"BAD_VALUE", "integer is not a valid signed 32-bit value"};
            return false;
        }
        output.type = I32;
        output.integerValue = *value;
    }
    else if (type == "F")
    {
        const std::optional<float> value = parseFloat32(*decoded);
        if (!value)
        {
            error = {"BAD_VALUE", "float is invalid, infinite, or NaN"};
            return false;
        }
        output.type = F32;
        output.floatValue = *value;
    }
    else if (type == "S")
    {
        if (decoded->find('\0') != std::string::npos)
        {
            error = {"BAD_VALUE", "string contains an embedded NUL byte"};
            return false;
        }
        output.type = S32;
        output.stringValue = *decoded;
    }
    return true;
}

bool parseSetBatch(
    const TokenIndex& tokenIndex,
    const std::vector<std::string_view>& fields,
    std::vector<ParsedSetValue>& values,
    ValidationError& error)
{
    if (fields.size() < 5U || (fields.size() - 2U) % 3U != 0U)
    {
        error = {
            "BAD_COMMAND",
            "SETMANY requires one or more token, type, base64-value triples"};
        return false;
    }

    const std::size_t itemCount = (fields.size() - 2U) / 3U;
    if (itemCount > maximumBatchItems)
    {
        error = {"BATCH_TOO_LARGE", "SETMANY is limited to 512 items"};
        return false;
    }

    values.clear();
    values.reserve(itemCount);
    std::vector<wtoken> seenTokens;
    seenTokens.reserve(itemCount);

    // Validate the complete batch before constructing any WAPI updates. A single
    // malformed or duplicate token rejects the command without a partial write.
    for (std::size_t itemIndex = 0; itemIndex < itemCount; ++itemIndex)
    {
        const std::size_t fieldIndex = 2U + itemIndex * 3U;
        ParsedSetValue value;
        ValidationError itemError;
        if (!parseSetValue(
                tokenIndex,
                fields[fieldIndex],
                fields[fieldIndex + 1U],
                fields[fieldIndex + 2U],
                value,
                itemError))
        {
            values.clear();
            error = {
                std::move(itemError.code),
                "SETMANY item " + std::to_string(itemIndex + 1U) + ": " +
                    itemError.message};
            return false;
        }

        if (std::find(seenTokens.begin(), seenTokens.end(), value.token) != seenTokens.end())
        {
            values.clear();
            error = {
                "DUPLICATE_TOKEN",
                "SETMANY contains the same token more than once"};
            return false;
        }
        seenTokens.push_back(value.token);
        values.push_back(std::move(value));
    }
    return true;
}

class ProtocolWriter
{
public:
    void ready() const
    {
        write(std::string(protocolReady));
    }

    void ok(std::string_view identifier) const
    {
        write("OK|" + std::string(identifier));
    }

    void error(
        std::string_view identifier,
        std::string_view code,
        std::string_view message) const
    {
        write(
            "ERROR|" + std::string(identifier) + "|" + std::string(code) + "|" +
            base64Encode(message));
    }

    void state(
        std::string_view identifier,
        std::string_view status,
        std::string_view detail) const
    {
        write(
            "STATE|" + std::string(identifier) + "|" + std::string(status) + "|" +
            base64Encode(detail));
    }

    void item(
        std::string_view identifier,
        std::string_view tokenName,
        char type,
        std::string_view value) const
    {
        write(
            "ITEM|" + std::string(identifier) + "|" + std::string(tokenName) + "|" +
            std::string(1, type) + "|" + base64Encode(value));
    }

    void end(std::string_view identifier, std::size_t count) const
    {
        write(
            "END|" + std::string(identifier) + "|" + std::to_string(count));
    }

    void event(std::string_view tokenName, char type, std::string_view value) const
    {
        write(
            "EVENT|" + std::string(tokenName) + "|" + std::string(1, type) + "|" +
            base64Encode(value));
    }

private:
    static void write(const std::string& line)
    {
        std::cout << line << '\n';
        std::cout.flush();
    }
};

class WapiHost
{
public:
    explicit WapiHost(const TokenIndex& tokenIndex)
        : tokenIndex_(tokenIndex)
    {
    }

    ~WapiHost()
    {
        closeConnection();
    }

    bool handle(std::string line)
    {
        if (line.size() >= 3U &&
            static_cast<unsigned char>(line[0]) == 0xefU &&
            static_cast<unsigned char>(line[1]) == 0xbbU &&
            static_cast<unsigned char>(line[2]) == 0xbfU)
        {
            line.erase(0, 3U);
        }

        if (!line.empty() && line.back() == '\r')
        {
            line.pop_back();
        }

        if (line.size() > maximumCommandLength)
        {
            writer_.error("", "COMMAND_TOO_LONG", "command exceeds 1 MiB");
            return true;
        }

        const std::vector<std::string_view> fields = splitFields(line);
        const std::string_view command = fields.empty() ? std::string_view{} : fields[0];

        if (command == "HELLO")
        {
            if (fields.size() != 1U)
            {
                writer_.error("", "BAD_COMMAND", "HELLO takes no arguments");
            }
            else
            {
                writeCurrentState();
            }
            return true;
        }

        const std::string_view identifier = fields.size() > 1U ? fields[1] : std::string_view{};
        if (!isValidIdentifier(identifier))
        {
            writer_.error("", "BAD_ID", "identifier must be 1-128 printable ASCII characters");
            return true;
        }

        if (command == "CONNECT")
        {
            handleConnect(fields, identifier);
        }
        else if (command == "DISCONNECT")
        {
            handleDisconnect(fields, identifier);
        }
        else if (command == "SNAPSHOT")
        {
            handleSnapshot(fields, identifier);
        }
        else if (command == "SET")
        {
            handleSet(fields, identifier);
        }
        else if (command == "SETMANY")
        {
            handleSetMany(fields, identifier);
        }
        else if (command == "PING")
        {
            if (fields.size() != 2U)
            {
                writer_.error(identifier, "BAD_COMMAND", "PING requires exactly an identifier");
            }
            else
            {
                writer_.ok(identifier);
            }
        }
        else if (command == "QUIT")
        {
            if (fields.size() != 2U)
            {
                writer_.error(identifier, "BAD_COMMAND", "QUIT requires exactly an identifier");
            }
            else
            {
                const bool wasConnected = connected_;
                closeConnection();
                writer_.ok(identifier);
                if (wasConnected)
                {
                    writer_.state(identifier, "DISCONNECTED", "quit requested");
                }
                return false;
            }
        }
        else
        {
            writer_.error(identifier, "BAD_COMMAND", "unknown protocol command");
        }
        return true;
    }

    void poll()
    {
        if (!connected_)
        {
            return;
        }

        // Keepalive and event draining share the host thread with commands. This
        // preserves the vendor library's single-threaded call boundary.
        const int keepAliveResult = wKeepAlive();
        if (keepAliveResult != WSUCCESS && keepAliveResult != WZERO)
        {
            failConnection(keepAliveResult, "keepalive");
            return;
        }

        std::array<wTV, eventBatchSize> events{};
        const int eventCount =
            wGetParsedEventsTimed(events.data(), eventBatchSize, eventPollTimeoutMicroseconds);
        if (eventCount < WZERO)
        {
            failConnection(eventCount, "event receive");
            return;
        }

        for (int index = 0; index < eventCount; ++index)
        {
            emitEvent(events[static_cast<std::size_t>(index)]);
            freeString(events[static_cast<std::size_t>(index)]);
        }
    }

private:
    void handleConnect(
        const std::vector<std::string_view>& fields,
        std::string_view identifier)
    {
        if (fields.size() != 3U)
        {
            writer_.error(identifier, "BAD_COMMAND", "CONNECT requires identifier and IPv4 address");
            return;
        }
        if (!isValidIpv4(fields[2]))
        {
            writer_.error(identifier, "BAD_IP", "address must be IPv4 or empty for discovery");
            return;
        }

        if (connected_)
        {
            if (fields[2].empty() || fields[2] == ip_)
            {
                writer_.ok(identifier);
                writer_.state(identifier, "CONNECTED", ip_);
            }
            else
            {
                writer_.error(
                    identifier,
                    "ALREADY_CONNECTED",
                    "this helper process already owns one WING connection");
            }
            return;
        }

        std::array<char, 64> address{};
        std::copy(fields[2].begin(), fields[2].end(), address.begin());
        const int result = wOpen(address.data());
        if (result != WSUCCESS)
        {
            writer_.error(
                identifier,
                "WAPI_" + std::to_string(result),
                "connect: " + wapiErrorMessage(result));
            writer_.state(identifier, "FAULTED", "connection failed");
            return;
        }

        connected_ = true;
        ip_ = address.data();
        writer_.ok(identifier);
        writer_.state(identifier, "CONNECTED", ip_);
    }

    void handleDisconnect(
        const std::vector<std::string_view>& fields,
        std::string_view identifier)
    {
        if (fields.size() != 2U)
        {
            writer_.error(identifier, "BAD_COMMAND", "DISCONNECT requires exactly an identifier");
            return;
        }
        closeConnection();
        writer_.ok(identifier);
        writer_.state(identifier, "DISCONNECTED", "disconnect requested");
    }

    void handleSnapshot(
        const std::vector<std::string_view>& fields,
        std::string_view identifier)
    {
        if (fields.size() != 3U)
        {
            writer_.error(identifier, "BAD_COMMAND", "SNAPSHOT requires identifier and token name");
            return;
        }
        if (!requireConnection(identifier))
        {
            return;
        }

        const std::optional<wtoken> token = tokenIndex_.find(fields[2]);
        if (!token)
        {
            writer_.error(identifier, "BAD_TOKEN", "unknown WAPI token name");
            return;
        }

        const wtype expectedType = wGetType(*token);
        if (expectedType == NODE)
        {
            snapshotNode(identifier, *token);
        }
        else
        {
            snapshotScalar(identifier, *token);
        }
    }

    void snapshotNode(std::string_view identifier, wtoken token)
    {
        std::vector<wTV> values(tokenIndex_.capacity());
        const int result = wGetNodeToTVArray(token, values.data());
        if (result < WZERO)
        {
            writer_.error(
                identifier,
                "WAPI_" + std::to_string(result),
                "snapshot node: " + wapiErrorMessage(result));
            return;
        }

        std::size_t emitted = 0;
        for (int index = 0; index < result; ++index)
        {
            const auto& value = values[static_cast<std::size_t>(index)];
            if (emitItem(identifier, value))
            {
                ++emitted;
            }
            freeString(values[static_cast<std::size_t>(index)]);
        }
        writer_.end(identifier, emitted);
    }

    void snapshotScalar(std::string_view identifier, wtoken token)
    {
        wtype type = UNKN;
        wvalue value{};
        const int result =
            wGetTokenTimed(token, &type, &value, scalarSnapshotTimeoutMicroseconds);
        if (result != WSUCCESS)
        {
            if (type == S32 && value.sval != nullptr)
            {
                std::free(value.sval);
            }
            writer_.error(
                identifier,
                "WAPI_" + std::to_string(result),
                "snapshot token: " + wapiErrorMessage(result));
            return;
        }

        wTV item{};
        item.token = token;
        item.type = type;
        if (type == I32)
        {
            item.d.idata = value.ival;
        }
        else if (type == F32)
        {
            item.d.fdata = value.fval;
        }
        else if (type == S32)
        {
            item.d.sdata = value.sval;
        }

        const bool emitted = emitItem(identifier, item);
        freeString(item);
        if (!emitted)
        {
            writer_.error(identifier, "BAD_TYPE", "snapshot returned unsupported WAPI type");
            return;
        }
        writer_.end(identifier, 1U);
    }

    void handleSet(
        const std::vector<std::string_view>& fields,
        std::string_view identifier)
    {
        if (fields.size() != 5U)
        {
            writer_.error(
                identifier,
                "BAD_COMMAND",
                "SET requires identifier, token name, type, and base64 value");
            return;
        }
        if (!requireConnection(identifier))
        {
            return;
        }

        ParsedSetValue value;
        ValidationError validationError;
        if (!parseSetValue(
                tokenIndex_,
                fields[2],
                fields[3],
                fields[4],
                value,
                validationError))
        {
            writer_.error(identifier, validationError.code, validationError.message);
            return;
        }

        int result = WTYPE;
        switch (value.type)
        {
        case I32:
            result = wSetTokenInt(value.token, value.integerValue);
            break;
        case F32:
            result = wSetTokenFloat(value.token, value.floatValue);
            break;
        case S32:
            result = wSetTokenString(value.token, value.stringValue.data());
            break;
        default:
            break;
        }

        if (result != WSUCCESS)
        {
            writer_.error(
                identifier,
                "WAPI_" + std::to_string(result),
                "set token: " + wapiErrorMessage(result));
            return;
        }
        writer_.ok(identifier);
    }

    void handleSetMany(
        const std::vector<std::string_view>& fields,
        std::string_view identifier)
    {
        std::vector<ParsedSetValue> values;
        ValidationError validationError;
        if (!parseSetBatch(tokenIndex_, fields, values, validationError))
        {
            writer_.error(identifier, validationError.code, validationError.message);
            return;
        }
        if (!requireConnection(identifier))
        {
            return;
        }

        std::vector<wTV> updates(values.size());
        for (std::size_t index = 0; index < values.size(); ++index)
        {
            ParsedSetValue& value = values[index];
            wTV& update = updates[index];
            update.token = value.token;
            update.type = value.type;
            if (value.type == I32)
            {
                update.d.idata = value.integerValue;
            }
            else if (value.type == F32)
            {
                update.d.fdata = value.floatValue;
            }
            else
            {
                update.d.sdata = value.stringValue.data();
            }
        }

        // All parsing, token, node, read-only, type, duplicate and size checks
        // complete before this single network-producing WAPI call.
        const int result =
            wSetNodeFromTVArray(updates.data(), static_cast<int>(updates.size()));
        if (result != WSUCCESS)
        {
            writer_.error(
                identifier,
                "WAPI_" + std::to_string(result),
                "set token batch: " + wapiErrorMessage(result));
            return;
        }
        writer_.ok(identifier);
    }

    [[nodiscard]] bool requireConnection(std::string_view identifier) const
    {
        if (!connected_)
        {
            writer_.error(identifier, "NOT_CONNECTED", "no WING is connected");
            return false;
        }
        return true;
    }

    bool emitItem(std::string_view identifier, const wTV& item) const
    {
        const std::string& name = tokenIndex_.canonicalName(item.token);
        switch (item.type)
        {
        case I32:
            writer_.item(identifier, name, 'I', std::to_string(item.d.idata));
            return true;
        case F32:
            writer_.item(identifier, name, 'F', formatFloat(item.d.fdata));
            return true;
        case S32:
            writer_.item(
                identifier,
                name,
                'S',
                item.d.sdata == nullptr ? std::string_view{} : std::string_view(item.d.sdata));
            return true;
        default:
            return false;
        }
    }

    void emitEvent(const wTV& event) const
    {
        const std::string& name = tokenIndex_.canonicalName(event.token);
        switch (event.type)
        {
        case I32:
            writer_.event(name, 'I', std::to_string(event.d.idata));
            break;
        case F32:
            writer_.event(name, 'F', formatFloat(event.d.fdata));
            break;
        case S32:
            writer_.event(
                name,
                'S',
                event.d.sdata == nullptr ? std::string_view{} : std::string_view(event.d.sdata));
            break;
        default:
            std::cerr << "Ignoring WAPI event with unsupported type "
                      << static_cast<int>(event.type) << '\n';
            break;
        }
    }

    static void freeString(wTV& value)
    {
        if (value.type == S32 && value.d.sdata != nullptr)
        {
            std::free(value.d.sdata);
            value.d.sdata = nullptr;
        }
    }

    void failConnection(int code, std::string_view operation)
    {
        const std::string message =
            std::string(operation) + ": " + wapiErrorMessage(code);
        writer_.error(
            "",
            "WAPI_" + std::to_string(code),
            message);
        closeConnection();
        writer_.state("", "FAULTED", message);
    }

    void closeConnection()
    {
        if (connected_)
        {
            const int result = wClose();
            if (result != WSUCCESS && result != WZERO)
            {
                std::cerr << "wClose failed with WAPI status " << result << '\n';
            }
        }
        connected_ = false;
        ip_.clear();
    }

    void writeCurrentState() const
    {
        if (connected_)
        {
            writer_.state("", "CONNECTED", ip_);
        }
        else
        {
            writer_.state("", "DISCONNECTED", "");
        }
    }

    const TokenIndex& tokenIndex_;
    ProtocolWriter writer_;
    bool connected_ = false;
    std::string ip_;
};

bool runSelfTest()
{
    const std::vector<std::string> codecSamples = {
        "",
        "a",
        "ab",
        "abc",
        "pipe|newline\nutf8:\xc3\xa9",
        std::string("\0\x01\xff", 3)};

    for (const std::string& sample : codecSamples)
    {
        const std::string encoded = base64Encode(sample);
        const std::optional<std::string> decoded = base64Decode(encoded);
        if (!decoded || *decoded != sample)
        {
            std::cerr << "self-test: base64 round-trip failed\n";
            return false;
        }
    }

    for (const std::string_view invalid : {"A", "A===", "####", "Zh==", "Zm9="})
    {
        if (base64Decode(invalid))
        {
            std::cerr << "self-test: invalid base64 was accepted\n";
            return false;
        }
    }

    const TokenIndex tokens;
    const auto jsonName = tokens.find("ch.1.fdr");
    const auto enumName = tokens.find("CH_1_FDR");
    if (!jsonName || !enumName || *jsonName != CH_1_FDR || *enumName != CH_1_FDR ||
        tokens.canonicalName(CH_1_FDR) != "ch.1.fdr")
    {
        std::cerr << "self-test: token index failed for ch.1.fdr / CH_1_FDR\n";
        return false;
    }

    if (tokens.size() < 35'000U || tokens.find("definitely.not.a.wapi.token"))
    {
        std::cerr << "self-test: token index size or negative lookup failed\n";
        return false;
    }

    ParsedSetValue parsedValue;
    ValidationError validationError;
    if (!parseSetValue(tokens, "CH_1_MUTE", "I", "MQ==", parsedValue, validationError) ||
        parsedValue.token != CH_1_MUTE || parsedValue.type != I32 ||
        parsedValue.integerValue != 1)
    {
        std::cerr << "self-test: SET value validation failed\n";
        return false;
    }
    const struct
    {
        std::string_view token;
        std::string_view wrongType;
        std::string_view encodedValue;
    } typeMismatches[] = {
        {"CH_1_MUTE", "S", "bm90LWEtbXV0ZQ=="},
        {"CH_1_FDR", "I", "MQ=="},
        {"CH_1_NAME", "F", "MS4w"},
    };
    for (const auto& mismatch : typeMismatches)
    {
        if (parseSetValue(
                tokens,
                mismatch.token,
                mismatch.wrongType,
                mismatch.encodedValue,
                parsedValue,
                validationError) ||
            validationError.code != "BAD_TYPE")
        {
            std::cerr << "self-test: mismatched WAPI SET type was accepted\n";
            return false;
        }
    }
    if (parseSetValue(tokens, "CH_40", "I", "MQ==", parsedValue, validationError) ||
        validationError.code != "BAD_TOKEN")
    {
        std::cerr << "self-test: node write validation failed\n";
        return false;
    }
    if (parseSetValue(
            tokens,
            "$STAT_A_STAT",
            "S",
            "T0s=",
            parsedValue,
            validationError) ||
        validationError.code != "READ_ONLY")
    {
        std::cerr << "self-test: read-only write validation failed\n";
        return false;
    }
    if (parseSetValue(
            tokens,
            "CH_1_NAME",
            "S",
            "not-base64",
            parsedValue,
            validationError) ||
        validationError.code != "BAD_BASE64")
    {
        std::cerr << "self-test: invalid SET base64 validation failed\n";
        return false;
    }

    const auto writableGlobal = tokens.find("$GLOBALS_CLKRATE");
    const auto writableSystem = tokens.find("$SYSCFG_IPMODE");
    if (!writableGlobal || !writableSystem ||
        tokens.isReadOnly(*writableGlobal) || tokens.isReadOnly(*writableSystem))
    {
        std::cerr << "self-test: writable dollar-root classification failed\n";
        return false;
    }

    const std::string validBatchLine =
        "SETMANY|test|CH_1_MUTE|I|MA==|CH_1_FDR|F|LTEyLjU=";
    const std::vector<std::string_view> validBatchFields = splitFields(validBatchLine);
    std::vector<ParsedSetValue> batchValues;
    if (!parseSetBatch(
            tokens,
            validBatchFields,
            batchValues,
            validationError) ||
        batchValues.size() != 2U)
    {
        std::cerr << "self-test: SETMANY validation failed\n";
        return false;
    }

    const std::string duplicateBatchLine =
        "SETMANY|test|CH_1_MUTE|I|MA==|ch.1.mute|I|MQ==";
    const std::vector<std::string_view> duplicateBatchFields =
        splitFields(duplicateBatchLine);
    if (parseSetBatch(
            tokens,
            duplicateBatchFields,
            batchValues,
            validationError) ||
        validationError.code != "DUPLICATE_TOKEN" ||
        !batchValues.empty())
    {
        std::cerr << "self-test: SETMANY duplicate validation failed\n";
        return false;
    }

    const std::string mismatchedTypeBatchLine =
        "SETMANY|test|CH_1_MUTE|I|MA==|CH_1_FDR|S|YmFk";
    const std::vector<std::string_view> mismatchedTypeBatchFields =
        splitFields(mismatchedTypeBatchLine);
    if (parseSetBatch(
            tokens,
            mismatchedTypeBatchFields,
            batchValues,
            validationError) ||
        validationError.code != "BAD_TYPE" ||
        !batchValues.empty())
    {
        std::cerr << "self-test: SETMANY mismatched type was not rejected atomically\n";
        return false;
    }

    std::string oversizedBatchLine = "SETMANY|test";
    for (std::size_t index = 0; index <= maximumBatchItems; ++index)
    {
        oversizedBatchLine += "|CH_1_MUTE|I|MA==";
    }
    const std::vector<std::string_view> oversizedBatchFields =
        splitFields(oversizedBatchLine);
    if (parseSetBatch(
            tokens,
            oversizedBatchFields,
            batchValues,
            validationError) ||
        validationError.code != "BATCH_TOO_LARGE")
    {
        std::cerr << "self-test: SETMANY size validation failed\n";
        return false;
    }

    const unsigned int version = wVer();
    std::cerr << "WapiHost self-test passed (WAPI "
              << ((version >> 24U) & 0xffU) << '.'
              << ((version >> 16U) & 0xffU) << '.'
              << ((version >> 8U) & 0xffU) << '-'
              << (version & 0xffU)
              << ", token slots " << tokens.size() << ")\n";
    return true;
}

int runHost()
{
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    std::ios::sync_with_stdio(false);
    std::cin.tie(nullptr);

    const TokenIndex tokenIndex;
    ProtocolWriter writer;
    writer.ready();

    CommandQueue queue;
    std::atomic_bool stopReader = false;

    // A dedicated reader prevents blocking stdin from starving WAPI keepalive and
    // event polling. It never calls the vendor library; it only fills this queue.
    std::thread reader([&queue, &stopReader]
    {
        std::string line;
        while (!stopReader.load(std::memory_order_acquire) && std::getline(std::cin, line))
        {
            {
                std::lock_guard lock(queue.mutex);
                queue.lines.push_back(std::move(line));
            }
            queue.changed.notify_one();
            line.clear();
        }

        {
            std::lock_guard lock(queue.mutex);
            queue.inputEnded = true;
        }
        queue.changed.notify_one();
    });

    const auto stopAndJoinReader = [&stopReader, &reader]
    {
        stopReader.store(true, std::memory_order_release);
        if (reader.joinable())
        {
            CancelSynchronousIo(static_cast<HANDLE>(reader.native_handle()));
            reader.join();
        }
    };

    try
    {
        WapiHost host(tokenIndex);
        bool keepRunning = true;
        while (keepRunning)
        {
            std::deque<std::string> pending;
            bool inputEnded = false;
            {
                std::lock_guard lock(queue.mutex);
                while (!queue.lines.empty() && pending.size() < maximumCommandsPerLoop)
                {
                    pending.push_back(std::move(queue.lines.front()));
                    queue.lines.pop_front();
                }
                inputEnded = queue.inputEnded && queue.lines.empty();
            }

            // Limit command work per turn so a busy managed client cannot starve
            // console events indefinitely.
            for (std::string& line : pending)
            {
                if (!host.handle(std::move(line)))
                {
                    keepRunning = false;
                    break;
                }
            }

            if (!keepRunning)
            {
                break;
            }
            if (inputEnded)
            {
                break;
            }

            host.poll();

            std::unique_lock lock(queue.mutex);
            queue.changed.wait_for(lock, idlePollInterval, [&queue]
            {
                return !queue.lines.empty() || queue.inputEnded;
            });
        }
    }
    catch (...)
    {
        stopAndJoinReader();
        throw;
    }

    stopAndJoinReader();
    return 0;
}
} // namespace

int main(int argc, char* argv[])
{
    if (argc == 2 && std::string_view(argv[1]) == "--self-test")
    {
        return runSelfTest() ? 0 : 1;
    }
    if (argc != 1)
    {
        std::cerr << "Usage: WingSync.WapiHost.exe [--self-test]\n";
        return 2;
    }

    try
    {
        return runHost();
    }
    catch (const std::exception& error)
    {
        std::cerr << "WapiHost fatal error: " << error.what() << '\n';
        return 3;
    }
    catch (...)
    {
        std::cerr << "WapiHost fatal unknown error\n";
        return 3;
    }
}
