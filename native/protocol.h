#pragma once
#include <cstdint>

constexpr uint32_t protocolMagic = 0x31435653;
enum class Operation : uint32_t { Reset = 1, Key = 2, Text = 3, Move = 4, Button = 5, Pulse = 6, Status = 7 };
#pragma pack(push, 1)
struct Command {
    uint32_t magic = protocolMagic;
    Operation operation{};
    uint64_t epoch{};
    uint64_t sequence{};
    int32_t a{}, b{};
    int32_t width{}, height{};
};
struct Reply {
    uint32_t magic = protocolMagic;
    uint32_t status{};
    uint64_t epoch{};
    uint64_t sequence{};
    uint32_t pid{};
    uint32_t hwnd{};
    int32_t width{}, height{};
    uint32_t keys{}, modifiers{};
    int32_t x{}, y{};
    uint64_t applied{};
};
#pragma pack(pop)
static_assert(sizeof(Command) == 40);
static_assert(sizeof(Reply) == 64);
