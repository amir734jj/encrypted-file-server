namespace Shared.Contracts;

public interface IGlobalConfigChangeHandler
{
    string OnChange(string value);
}
